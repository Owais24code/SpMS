using Spms.Api.Endpoints;
using Spms.Api.Http;
using Spms.Application;
using Spms.Domain.Audit;
using Spms.Domain.Concurrency;
using Spms.Domain.Errors;
using Spms.Domain.Permissions;
using Spms.Infrastructure;

namespace Spms.Api;

/// <summary>
/// Builds the host.
///
/// Extracted from Program so the test project can start the real application
/// rather than a hand-rolled approximation of it — the pipeline, middleware
/// and content negotiation are part of what needs testing.
/// </summary>
public static class HostFactory
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddSingleton<IClock, SystemClock>();
        builder.Services.AddSingleton<IAppointmentRepository, InMemoryAppointmentRepository>();
        builder.Services.AddSingleton<IAuditSink, InMemoryAuditSink>();
        builder.Services.AddSingleton<IIdempotencyStore, InMemoryIdempotencyStore>();
        builder.Services.AddSingleton<PreflightService>();
        builder.Services.AddSingleton<AppointmentService>();
        builder.Services.AddProblemDetails();

        builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins("http://localhost:4200", "https://localhost:4200")
            .AllowAnyHeader()
            .WithExposedHeaders("ETag")   // the browser cannot read ETag without this
            .AllowAnyMethod()));

        var app = builder.Build();

        app.UseCors();

        // Correlation id on every response, so a client-side error report can
        // be traced to a server log line without interviewing the user.
        app.Use(async (ctx, next) =>
        {
            var id = ctx.Request.Headers[CallerAccessor.CorrelationHeader].ToString();
            if (string.IsNullOrWhiteSpace(id)) id = ctx.TraceIdentifier;
            ctx.Response.Headers[CallerAccessor.CorrelationHeader] = id;
            await next();
        });

        app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

        // Effective operating mode (OFF-001..006). The client enforces what
        // the server publishes rather than guessing from navigator.onLine.
        app.MapGet("/operating-mode", () => Results.Ok(new
        {
            mode = "Online",
            read_only = false,
            queue_writes = false,
        }));

        app.MapGet("/audit", (IAuditSink sink, HttpContext ctx) =>
        {
            var caller = CallerAccessor.From(ctx);
            return caller.Has(SpmsScopes.Admin)
                ? Results.Ok(sink.Recent())
                : ProblemResults.From(SpmsProblem.AuthorizationDenied, caller.CorrelationId);
        });

        app.MapSchedule();
        app.MapAppointments();

        return app;
    }
}
