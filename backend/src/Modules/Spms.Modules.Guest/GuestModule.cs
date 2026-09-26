using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spms.Modules.Guest.Data;
using Spms.Modules.Guest.Endpoints;
using Spms.Modules.Guest.Identity;
using Spms.Modules.Guest.Profiles;
using Spms.SharedKernel;
using Spms.Persistence;

namespace Spms.Modules.Guest;

public static class GuestModule
{
    public static IServiceCollection AddGuestModule(this IServiceCollection services, GuestLinkOptions? links = null)
    {
        services.AddSingleton<IModelContributor, GuestModelContributor>();
        services.TryAddSingleton(links ?? new GuestLinkOptions());
        services.TryAddScoped<IGuestLinkSender, LoggingGuestLinkSender>();
        services.AddScoped<MagicLinkService>();
        services.AddScoped<GuestProfileService>();
        services.AddScoped<GuestMergeService>();
        services.AddScoped<DelegationService>();
        services.AddScoped<ConsentService>();
        services.AddScoped<PrivacyService>();
        services.AddScoped<IPropertyJob, DelegationExpiryJob>();
        return services;
    }

    public static IEndpointRouteBuilder MapGuestModule(this IEndpointRouteBuilder app)
    {
        app.MapGuestIdentity();
        app.MapGuestProfiles();
        return app;
    }
}

/// <summary>Active delegations past their end become Expired, and their OpenFGA tuples go (idempotent: tenant-wide rows, run per property).</summary>
public sealed class DelegationExpiryJob(DelegationService delegations) : IPropertyJob
{
    public string Name => "guest.delegation-expiry";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);
    public Task<int> RunAsync(CancellationToken ct) => delegations.ExpireAsync(ct);
}
