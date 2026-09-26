using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Commerce.Data;
using Spms.Persistence;

namespace Spms.Modules.Commerce;

public static class CommerceModule
{
    public static IServiceCollection AddCommerceModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CommerceModelContributor>();
        return services;
    }

    public static IEndpointRouteBuilder MapCommerceModule(this IEndpointRouteBuilder app) => app;
}
