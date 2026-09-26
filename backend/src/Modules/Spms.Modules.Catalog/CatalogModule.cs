using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Catalog.Data;
using Spms.Persistence;

namespace Spms.Modules.Catalog;

public static class CatalogModule
{
    public static IServiceCollection AddCatalogModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CatalogModelContributor>();
        services.AddScoped<CatalogService>();
        return services;
    }

    public static IEndpointRouteBuilder MapCatalogModule(this IEndpointRouteBuilder app) => app.MapCatalog();
}
