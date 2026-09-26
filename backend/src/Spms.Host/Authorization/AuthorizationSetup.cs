using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Spms.Host.Workers;
using Spms.Modules.Core.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host.Authorization;

public static class AuthorizationSetup
{
    public static IServiceCollection AddSpmsAuthorization(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var options = config.GetSection("Authorization").Get<AuthorizationOptions>() ?? new AuthorizationOptions();
        var fgaConfigured = !string.IsNullOrWhiteSpace(options.OpenFga.ApiUrl);

        if (!fgaConfigured && !env.IsDevelopment())
            throw new InvalidOperationException("Authorization:OpenFga:ApiUrl is required outside Development (OpenFGA decides access; there is no fallback).");
        if (options.Mode == "Permissive" && !env.IsDevelopment())
            throw new InvalidOperationException("Authorization:Mode=Permissive is refused outside Development.");

        services.AddSingleton(options);
        services.AddSingleton(options.OpenFga);
        services.AddSingleton<FgaClientHolder>();
        services.AddSingleton<FgaTupleWriter>();
        services.AddSingleton<FgaReconciler>();
        services.AddSingleton<IOutboxHandler, FgaTupleSyncHandler>();

        if (fgaConfigured && options.Mode != "Permissive") services.AddSingleton<IAccessDecider, OpenFgaAccessDecider>();
        else services.AddSingleton<IAccessDecider, PermissiveAccessDecider>();

        var outbox = config.GetSection("Outbox").Get<OutboxOptions>() ?? new OutboxOptions();
        services.AddSingleton(outbox);
        services.AddSingleton<OutboxDispatcher>();
        if (outbox.Enabled) services.AddHostedService<OutboxWorker>();
        return services;
    }

    /// <summary>Development: bootstrap the OpenFGA store when asked, then reconcile every tuple from the tables.</summary>
    public static async Task StartAuthorizationAsync(this WebApplication app)
    {
        var holder = app.Services.GetRequiredService<FgaClientHolder>();
        var options = app.Services.GetRequiredService<OpenFgaOptions>();
        if (app.Services.GetRequiredService<IAccessDecider>() is PermissiveAccessDecider)
        {
            app.Logger.LogWarning("Authorization is PERMISSIVE: no OpenFGA server is configured, so relationship checks allow everything (Development only).");
            return;
        }
        if (!holder.Ready && options.Bootstrap) await holder.BootstrapAsync(app.Logger);
        if (!holder.Ready) throw new InvalidOperationException("Authorization:OpenFga:StoreId is not set and Bootstrap is off.");
        if (options.Bootstrap) await app.Services.GetRequiredService<FgaReconciler>().RunAsync();
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app)
    {
        // Who SpMS thinks the caller is: what the front end renders its shell
        // from (name, roles, scopes, the properties it may switch between).
        app.MapGet("/me", async Task<IResult> (HttpContext http, SpmsDbContext db, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (!ctx.Authenticated)
                return Problem.From(ApiError.AuthenticationRequired, ctx.CorrelationId, "Present a bearer token.");

            var ids = ctx.PropertyIds.ToArray();
            var properties = await db.Set<PropertyRow>().AsNoTracking()
                .Where(p => ids.Contains(p.PropertyId))
                .OrderBy(p => p.Name)
                .Select(p => new { propertyId = p.PropertyId, p.Code, p.Name, p.Timezone, p.OperatingMode })
                .ToListAsync(ct);

            // The operator's staff record, if any: the provider tablet lists "my" appointments by it.
            var staffId = ctx.ActorType == Spms.SharedKernel.ActorType.Staff
                ? await db.Set<Spms.Modules.Workforce.Data.StaffRow>().AsNoTracking()
                    .Where(s => s.PrincipalId == ctx.PrincipalId).Select(s => (Guid?)s.StaffId).FirstOrDefaultAsync(ct)
                : null;

            return Results.Json(new
            {
                principalId = ctx.PrincipalId,
                staffId,
                displayName = ctx.Actor,
                actorType = ctx.ActorType.ToString(),
                tenantId = ctx.TenantId,
                propertyId = ctx.PropertyId,
                guestId = ctx.GuestId,
                roles = http.User.FindAll(SpmsClaims.Role).Select(c => c.Value).Distinct().Order().ToArray(),
                scopes = ctx.Scopes.Order().ToArray(),
                properties,
            }, Json.Options);
        });
        return app;
    }
}
