using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Core.Data;
using Spms.Modules.Core.Infrastructure;
using Spms.Modules.Core.Settings;
using Spms.Modules.Core.Integrations;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Core;

public static class CoreModule
{
    public static IServiceCollection AddCoreModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CoreModelContributor>();
        services.AddScoped<IAuditSink, EfAuditSink>();
        services.AddScoped<SettingsReader>();
        services.AddScoped<SettingsAdmin>();
        services.AddScoped<DeviceService>();
        services.AddScoped<IntegrationService>();
        services.AddScoped<IPropertyJob, SettingActivationJob>();
        services.AddScoped<IOutbox, EfOutbox>();
        services.AddScoped<AuditQueries>();
        services.AddSingleton<IIdempotencyStore, PostgresIdempotencyStore>();
        return services;
    }

    public static IEndpointRouteBuilder MapCoreModule(this IEndpointRouteBuilder app)
    {
        app.MapGet("/audit", async (HttpContext http, AuditQueries audit, int? limit, string? entityId, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (Guard.RequireScope(ctx, SpaScopes.Admin) is { } denied) return denied;
            var take = Math.Clamp(limit ?? 50, 1, PageLimits.Max);
            var items = await audit.RecentAsync(ctx.PropertyId, take, entityId, ct);
            return Results.Json(new { items, count = items.Count }, Json.Options);
        });
        app.MapSettings();
        app.MapIntegrations();
        return app;
    }
}
