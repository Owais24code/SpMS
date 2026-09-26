using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spms.Modules.Guest.Data;
using Spms.Modules.Guest.Endpoints;
using Spms.Modules.Guest.Identity;
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
        return services;
    }

    public static IEndpointRouteBuilder MapGuestModule(this IEndpointRouteBuilder app)
    {
        app.MapGuestIdentity();
        return app;
    }
}
