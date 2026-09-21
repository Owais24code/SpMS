using Spms.Api.Endpoints;
using Spms.Api.Infrastructure;
using Spms.Domain.Abstractions;
using Spms.Domain.Errors;
using Spms.Domain.Scheduling;
using Spms.Infrastructure.InMemory;

var builder = WebApplication.CreateBuilder(args);

// Storage ports. The in-memory adapters ARE the store, so they are singletons.
// SchedulingService is Scoped now rather than later: it captures the three
// ports, and registering it as a singleton would become a captive-dependency
// failure at first resolve the moment those become scoped under EF Core.
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IAppointmentRepository, InMemoryAppointmentRepository>();
builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
builder.Services.AddSingleton<IPreflightStore, InMemoryPreflightStore>();
builder.Services.AddSingleton<IAuditSink, InMemoryAuditSink>();
builder.Services.AddScoped<SchedulingService>();

// CORS origins come from configuration; they were compiled in, so a deployed
// build could only ever talk to a developer's localhost.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
    ?? ["http://localhost:4200", "http://127.0.0.1:4200"];

builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .WithExposedHeaders("ETag", "Location", "X-Correlation-Id", "Retry-After")));

var app = builder.Build();

// Header-supplied identity exists only in Development. Trusting X-Spa-Scopes
// in any other environment would let any caller grant themselves spa.admin
// with a curl flag; outside Development every scoped endpoint answers 401
// until real JWT middleware is wired.
if (app.Environment.IsDevelopment())
{
    RequestContext.EnableDevHeaderAuth();
    app.Logger.LogWarning("Development header auth is ENABLED. X-Spa-Scopes is trusted on every request.");
}
else
{
    app.Logger.LogInformation("Header auth disabled outside Development; scoped endpoints will answer 401.");
}

app.UseCors();

// Correlation on every response, including failures — support cannot chase a
// problem report that has no id in it. RequestContext.From caches per request,
// so this id is the same one every endpoint and log line below sees.
app.Use(async (http, next) =>
{
    var ctx = RequestContext.From(http);
    http.Response.Headers["X-Correlation-Id"] = ctx.CorrelationId;

    // Puts the correlation id on every log line for the request, which is what
    // makes it usable for the support lookup it exists for.
    using var scope = app.Logger.BeginScope(new Dictionary<string, object>
    {
        ["CorrelationId"] = ctx.CorrelationId,
        ["TenantId"] = ctx.TenantId,
        ["PropertyId"] = ctx.PropertyId,
    });

    try
    {
        await next();
    }
    catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested)
    {
        // The client hung up. Not a defect, and there is nobody to answer.
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
        // Never echo exception text: it leaks internals and sometimes PII.
        var result = Problem.From(ApiError.InternalError, ctx.CorrelationId,
            "The request could not be completed. Quote the correlation id if this persists.");
        await result.ExecuteAsync(http);
    }
});

app.MapMeta(app.Environment);
app.MapAppointments();
app.MapScheduling();

if (app.Environment.IsDevelopment())
    await DevSeed.ApplyAsync(app.Services);

await app.RunAsync();
