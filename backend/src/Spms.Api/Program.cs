using Microsoft.EntityFrameworkCore;
using Npgsql;
using Spms.Api.Endpoints;
using Spms.Api.Infrastructure;
using Spms.Domain.Abstractions;
using Spms.Domain.Errors;
using Spms.Domain.Scheduling;
using Spms.Infrastructure.Postgres;

var builder = WebApplication.CreateBuilder(args);

/* ------------------------------ persistence ------------------------------ */

var connectionString =
    builder.Configuration.GetConnectionString("Spms")
    ?? Environment.GetEnvironmentVariable("SPMS_CONNECTION")
    ?? throw new InvalidOperationException(
        "No database connection string. Set ConnectionStrings:Spms or SPMS_CONNECTION. " +
        "There is no in-memory fallback on purpose: a process that silently starts with a store " +
        "that loses every appointment on restart is worse than one that refuses to start.");

// One data source for the whole process: it owns the connection pool, and the
// idempotency store needs a connection OUTSIDE the business transaction.
builder.Services.AddSingleton(_ =>
{
    var b = new NpgsqlDataSourceBuilder(connectionString);
    return b.Build();
});

builder.Services.AddDbContext<SpmsDbContext>((sp, o) =>
{
    o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npg => npg.EnableRetryOnFailure(0));
    // A silent client-side evaluation of a filter is a correctness bug in a
    // multi-tenant read, not a performance note.
    o.ConfigureWarnings(w => w.Throw(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.MultipleCollectionIncludeWarning));
});

builder.Services.AddSingleton<IClock, SystemClock>();

// Scoped, because they hold the request's DbContext and therefore its
// transaction. SchedulingService was a singleton capturing these; the moment
// persistence became scoped that would have been a captive dependency and a
// DI validation failure at first resolve.
builder.Services.AddScoped<IUnitOfWork, PostgresUnitOfWork>();
builder.Services.AddScoped<IAppointmentRepository, PostgresAppointmentRepository>();
builder.Services.AddScoped<IPreflightStore, PostgresPreflightStore>();
builder.Services.AddScoped<IAuditSink, PostgresAuditSink>();
builder.Services.AddScoped<IServiceCatalog, PostgresServiceCatalog>();
builder.Services.AddScoped<IQualificationRegister, PostgresQualificationRegister>();
builder.Services.AddScoped<IPropertyDirectory, PostgresPropertyDirectory>();
builder.Services.AddScoped<SchedulingService>();

// Singleton on its own connection, deliberately outside any business
// transaction: a refused request must not roll back its own replay record.
builder.Services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();

builder.Services.AddSingleton<ILoggerLike, ConsoleLoggerLike>();
builder.Services.AddSingleton<MigrationRunner>();
builder.Services.AddSingleton<SchemaGuard>();

/* --------------------------------- auth --------------------------------- */

builder.Services.AddSpmsAuthentication(builder.Configuration, builder.Environment);
builder.Services.AddAuthorization();

/* --------------------------------- cors --------------------------------- */

var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:4200", "http://127.0.0.1:4200"];

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("ETag", "Location", "X-Correlation-Id", "Retry-After")));

var app = builder.Build();

/* ----------------------------- startup gates ----------------------------- */

// Migrations run at startup only where it is safe to do so. In a deployed
// environment the deployment pipeline applies them, because two instances
// starting together would both try, and a schema change is not something an
// app instance should decide to perform.
if (app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
{
    var applied = await app.Services.GetRequiredService<MigrationRunner>().RunAsync();
    foreach (var a in applied.Where(a => !a.AlreadyPresent))
        app.Logger.LogInformation("Applied migration {Version} ({File})", a.Version, a.FileName);
}

// The schema the code depends on either exists or the process does not start.
// Refusing to boot is the point: an instance that starts without the
// room-overlap constraint would go on evaluating conflicts and be
// systematically wrong about the one conflict it cannot enforce itself.
var schemaProblems = await app.Services.GetRequiredService<SchemaGuard>().VerifyAsync();
if (schemaProblems.Count > 0)
{
    foreach (var p in schemaProblems) app.Logger.LogCritical("Schema check failed: {Problem}", p);
    throw new InvalidOperationException(
        $"The database is missing {schemaProblems.Count} required structure(s): {string.Join("; ", schemaProblems)}");
}

if (app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "Development: the {Scheme} authentication scheme is enabled and X-Spa-Scopes is trusted.", SpmsAuth.DevScheme);
    await DevSeed.ApplyAsync(app.Services);
}

app.UseCors();

// Correlation on every response, including failures — support cannot chase a
// problem report that has no id in it.
app.Use(async (http, next) =>
{
    var ctx = RequestContext.From(http);
    http.Response.Headers["X-Correlation-Id"] = ctx.CorrelationId;

    using var scope = app.Logger.BeginScope(new Dictionary<string, object>
    {
        ["CorrelationId"] = ctx.CorrelationId,
    });

    try
    {
        await next();
    }
    catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
    {
        app.Logger.LogInformation("Request aborted by client {CorrelationId}", ctx.CorrelationId);
    }
    catch (Exception ex)
    {
        app.Logger.LogError(ex, "Unhandled failure {CorrelationId}", ctx.CorrelationId);
        if (http.Response.HasStarted) throw;

        http.Response.Clear();
        // Clear() drops the headers set above, so the correlation id has to be
        // re-set or the one response that most needs it arrives without it.
        http.Response.Headers["X-Correlation-Id"] = ctx.CorrelationId;

        // INTERNAL_ERROR, not DEPENDENCY_TIMEOUT: labelling our own defects as
        // a dependency being slow sent every investigation to the wrong team
        // and made the 503 retry advice actively wrong.
        var result = Problem.From(ApiError.InternalError, ctx.CorrelationId,
            "The request could not be completed. Quote the correlation id if this persists.");
        await result.ExecuteAsync(http);
    }
});

app.UseAuthentication();
app.UseAuthorization();

app.MapMeta(app.Environment);
app.MapAppointments();
app.MapScheduling();

await app.RunAsync();
