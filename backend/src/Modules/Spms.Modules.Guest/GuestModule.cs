using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Guest.Data;
using Spms.Persistence;

namespace Spms.Modules.Guest;

public static class GuestModule
{
    public static IServiceCollection AddGuestModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, GuestModelContributor>();
        return services;
    }

    public static IEndpointRouteBuilder MapGuestModule(this IEndpointRouteBuilder app) => app;
}
