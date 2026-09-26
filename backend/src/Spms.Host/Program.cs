using Spms.Host.Operations;
using Npgsql;
using Spms.Host;
using Spms.Host.Authorization;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

var builder = WebApplication.CreateBuilder(args);

/* ------------------------------ persistence ------------------------------ */

var connectionString =
    builder.Configuration.GetConnectionString("Spms")
    ?? Environment.GetEnvironmentVariable("SPMS_CONNECTION")
    ?? throw new InvalidOperationException(
        "No database connection string. Set ConnectionStrings:Spms or SPMS_CONNECTION. " +
        "There is no in-memory fallback on purpose: a process that silently starts with a store " +
        "that loses every appointment on restart is worse than one that refuses to start.");
if (string.IsNullOrWhiteSpace(connectionString))
    throw new InvalidOperationException("ConnectionStrings:Spms is empty.");

builder.Services.AddSpmsPersistence(connectionString, o =>
{
    o.RuntimeRole = builder.Configuration["Database:RuntimeRole"] ?? "spms_app";
    o.RequireScopedTransaction = true;
});

builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSpmsProtection(builder.Configuration, builder.Environment);
// Guest magic links: where the guest web lives, and (Development only) logging links instead of sending them.
builder.Services.AddSingleton(builder.Configuration.GetSection("Guest:Links").Get<Spms.Modules.Guest.Identity.GuestLinkOptions>()
    ?? new Spms.Modules.Guest.Identity.GuestLinkOptions { LogLinks = builder.Environment.IsDevelopment() });
builder.Services.AddSingleton(builder.Configuration.GetSection("Scheduling").Get<Spms.Modules.Scheduling.Domain.SchedulingOptions>()
    ?? new Spms.Modules.Scheduling.Domain.SchedulingOptions());
builder.Services.AddSpmsModules();

// Per-property housekeeping jobs (hold expiry, preflight expiry, waitlist offers).
var jobs = builder.Configuration.GetSection("Jobs").Get<Spms.Host.Workers.JobOptions>() ?? new Spms.Host.Workers.JobOptions();
builder.Services.AddSingleton(jobs);
builder.Services.AddSingleton<Spms.Host.Workers.JobRunner>();
if (jobs.Enabled) builder.Services.AddHostedService<Spms.Host.Workers.JobWorker>();
var maintenance = builder.Configuration.GetSection("Maintenance").Get<Spms.Host.Workers.MaintenanceOptions>() ?? new Spms.Host.Workers.MaintenanceOptions();
builder.Services.AddSingleton(maintenance);
if (builder.Environment.IsDevelopment() && !args.Contains("--migrate") && !args.Contains("--maintain"))
    builder.Services.AddHostedService<Spms.Host.Workers.MaintenanceWorker>();

/* ----------------------------- observability ----------------------------- */

var observability = builder.Configuration.GetSection("Observability").Get<ObservabilityOptions>() ?? new ObservabilityOptions();
builder.Services.AddSingleton(observability);
builder.Services.AddSingleton<MetricsCollector>();
builder.Services.AddHostedService<MetricsSampler>();
// Structured logs outside Development: one JSON object per line, UTC, with the request's correlation and trace ids.
var logFormat = observability.LogFormat is { Length: > 0 } f ? f : builder.Environment.IsDevelopment() ? "simple" : "json";
if (logFormat == "json")
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o => { o.IncludeScopes = true; o.UseUtcTimestamp = true; o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ"; });
}

/* --------------------------------- auth --------------------------------- */

builder.Services.AddSpmsAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization();
builder.Services.AddSpmsAuthorization(builder.Configuration, builder.Environment);

/* --------------------------------- cors --------------------------------- */

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
if (allowedOrigins.Length == 0 && builder.Environment.IsDevelopment())
    allowedOrigins = ["http://localhost:4200", "http://127.0.0.1:4200"];

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("ETag", "Location", "X-Correlation-Id", "Retry-After")));

var app = builder.Build();

/* ----------------------------- startup gates ----------------------------- */

var ownerConnection = app.Configuration.GetConnectionString("SpmsOwner") ?? connectionString;
var adminConnection = app.Configuration.GetConnectionString("SpmsAdmin");

// `Spms.Host --migrate` is how a pipeline applies the schema: roles (when an
// admin connection is given), then the EF migrations as spms_owner, then exit.
if (args.Contains("--migrate"))
{
    if (!string.IsNullOrWhiteSpace(adminConnection))
    {
        await DatabaseBootstrapper.EnsureRolesAsync(adminConnection);
        var logins = new List<(string, string, string[])>();
        foreach (var (key, roles) in new[] { ("Owner", new[] { "spms_owner" }), ("App", new[] { "spms_app", "spms_outbox" }) })
            if (app.Configuration[$"Database:Logins:{key}:Name"] is { Length: > 0 } login && app.Configuration[$"Database:Logins:{key}:Password"] is { Length: > 0 } password)
                logins.Add((login, password, roles));
        if (logins.Count > 0) await DatabaseBootstrapper.EnsureLoginsAsync(adminConnection, logins);
    }
    var applied = await DatabaseBootstrapper.MigrateAsync(ownerConnection, SpmsModules.Contributors(),
        app.Configuration["Database:MigrationRole"] ?? "spms_owner");
    app.Logger.LogInformation("Applied {Count} migration(s): {Names}", applied.Count, string.Join(", ", applied));
    return;
}

// In Development only, the instance may bring its own database up to date.
// Deployed environments migrate from the pipeline: two instances starting
// together would race, and a schema change is not an instance's decision.
if (app.Configuration.GetValue("Database:MigrateOnStartup", false))
{
    await DatabaseBootstrapper.EnsureRolesAsync(adminConnection ?? ownerConnection);
    var applied = await DatabaseBootstrapper.MigrateAsync(ownerConnection, SpmsModules.Contributors(),
        app.Configuration["Database:MigrationRole"] ?? "spms_owner");
    foreach (var m in applied) app.Logger.LogInformation("Applied migration {Migration}", m);
}

// The schema the code depends on either exists or the process does not start:
// every migration applied, and the EF model identical to the live tables. An
// instance that starts against a drifted schema would be systematically wrong
// about whatever drifted.
await SchemaGate.VerifyAsync(app.Services, ownerConnection, app.Logger);

// `Spms.Host --maintain` is the scheduled database housekeeping (partitions
// ahead, published outbox months dropped), run as the owner, then exit.
if (args.Contains("--maintain"))
{
    var report = await Spms.Host.Workers.DatabaseMaintenance.RunAsync(ownerConnection, app.Configuration["Database:MigrationRole"] ?? "spms_owner",
        maintenance, app.Logger);
    app.Logger.LogInformation("Maintenance: {Created} created, {Dropped} dropped, {Warnings} warning(s)", report.Created.Count, report.Dropped.Count, report.Warnings.Count);
    return;
}

// `Spms.Host --fga-sync` rewrites every OpenFGA tuple from the tables (after a
// store restore, or to repair drift), then exits.
if (args.Contains("--fga-sync"))
{
    await app.Services.GetRequiredService<Spms.Host.Authorization.FgaClientHolder>().BootstrapIfNeededAsync(app);
    await app.Services.GetRequiredService<Spms.Host.Authorization.FgaReconciler>().RunAsync();
    return;
}

if (app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "Development: the {Scheme} authentication scheme is enabled and X-Spa-* headers are trusted.", SpmsAuth.DevScheme);
    await DevSeed.ApplyAsync(ownerConnection, app.Configuration["Database:MigrationRole"] ?? "spms_owner");
}

await app.StartAuthorizationAsync();

app.UseCors();

// Correlation on every response, including failures — support cannot chase a
// problem report that has no id in it.
app.Use(async (http, next) =>
{
    var correlationId = RequestContext.CorrelationOf(http);
    http.Response.Headers["X-Correlation-Id"] = correlationId;

    using var scope = app.Logger.BeginScope(new Dictionary<string, object>
    {
        ["CorrelationId"] = correlationId,
        ["TraceId"] = System.Diagnostics.Activity.Current?.TraceId.ToString() ?? "",
    });
    try
    {
        await next();
    }
    catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogInformation("Request aborted by client {CorrelationId}", correlationId);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled failure {CorrelationId}", correlationId);
        if (http.Response.HasStarted) throw;

        http.Response.Clear();
        // Clear() drops the headers set above, so the correlation id has to be
        // re-set or the one response that most needs it arrives without it.
        http.Response.Headers["X-Correlation-Id"] = correlationId;
        await Problem.From(ApiError.InternalError, correlationId,
            "The request could not be completed. Quote the correlation id if this persists.").ExecuteAsync(http);
    }
});

app.UseRequestMetrics();
app.UseAuthentication();
app.UseAuthorization();

app.MapMeta(app.Environment);
app.MapMetrics(app.Environment, observability);

// Every module endpoint runs inside one scoped request transaction.
var api = app.MapGroup("").AddEndpointFilter<RequestTransactionFilter>();
api.MapIdentityEndpoints();
api.MapSpmsModules();
api.MapSearch();
// Streams are outside the request transaction: a connection that lives for hours must not hold one.
app.MapBoardStream();

await app.RunAsync();

/// <summary>Exposed for WebApplicationFactory-style tests.</summary>
public partial class Program;
