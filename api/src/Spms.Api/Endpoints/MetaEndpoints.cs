using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Spms.Api.Infrastructure;
using Spms.Domain.Abstractions;
using Spms.Domain.Permissions;
using Spms.Domain.Scheduling;

namespace Spms.Api.Endpoints;

/// <summary>Health, reference data and the audit trail.</summary>
public static class MetaEndpoints
{
    public static void MapMeta(this IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        // Unauthenticated on purpose: a liveness probe has no token, and the
        // body carries nothing a caller could not already infer.
        app.MapGet("/health/live", (IClock clock) => Results.Json(new
        {
            status = "ok",
            utc = clock.UtcNow.ToString("O"),
        }, Json.Options));

        // Readiness is separate because a process that is listening is not the
        // same as a process that can serve. With a database it probes it; the
        // in-memory store is always ready, and says which store it is rather
        // than hard-coding a persistence claim that the EF adapter will falsify.
        app.MapGet("/health/ready", (IClock clock, IAppointmentRepository repo) => Results.Json(new
        {
            status = "ok",
            utc = clock.UtcNow.ToString("O"),
            release = "R1-slice",
            persistence = repo.GetType().Name,
        }, Json.Options));

        // Kept for existing probes and the front end.
        app.MapGet("/health", (IClock clock, IAppointmentRepository repo) => Results.Json(new
        {
            status = "ok",
            utc = clock.UtcNow.ToString("O"),
            release = "R1-slice",
            persistence = repo.GetType().Name,
        }, Json.Options));

        // Development only. The 500 path — Response.Clear(), re-setting the
        // correlation header, INTERNAL_ERROR rather than a dependency timeout —
        // is the one path no client can reach on purpose, so without this it
        // was shipped on inspection alone. Registered from the injected
        // environment rather than a static flag, so it does not depend on
        // statement order in Program.cs.
        if (environment.IsDevelopment())
        {
            app.MapGet("/dev/throw", IResult () =>
                throw new InvalidOperationException("Deliberate failure exercising the error handler."));
        }

        app.MapGet("/services", Services);
        app.MapGet("/audit", Audit);
    }

    private static IResult Services(HttpContext http)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Read) is { } denied) return denied;
        return Results.Json(ServiceCatalog.Services, Json.Options);
    }

    private static async Task<IResult> Audit(
        HttpContext http, IAuditSink audit, int? limit, CancellationToken ct)
    {
        var ctx = RequestContext.From(http);
        if (Guard.RequireScope(ctx, SpaScopes.Admin) is { } denied) return denied;

        // Scoped by tenant AND property. Tenant-only scoping let an admin at
        // one property read every other property's actor names, subject ids
        // and correlation ids.
        var take = Math.Clamp(limit ?? 50, 1, PageLimits.Max);
        var rows = await audit.RecentAsync(ctx.TenantId, ctx.PropertyId, take, ct);
        var items = rows.Select(AuditEntryDto.From).ToList();
        return Results.Json(new { items, count = items.Count }, Json.Options);
    }
}
