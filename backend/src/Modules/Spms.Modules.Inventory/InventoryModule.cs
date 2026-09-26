using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Inventory.Data;
using Spms.Persistence;

namespace Spms.Modules.Inventory;

public static class InventoryModule
{
    public static IServiceCollection AddInventoryModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, InventoryModelContributor>();
        return services;
    }

    public static IEndpointRouteBuilder MapInventoryModule(this IEndpointRouteBuilder app) => app;
}
