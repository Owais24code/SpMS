using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Resources.Data;
using Spms.Persistence;

namespace Spms.Modules.Resources;

public static class ResourcesModule
{
    public static IServiceCollection AddResourcesModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, ResourcesModelContributor>();
        services.AddScoped<ResourceService>();
        return services;
    }

    public static IEndpointRouteBuilder MapResourcesModule(this IEndpointRouteBuilder app) => app.MapResources();
}
