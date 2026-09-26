using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;

namespace Spms.Modules.Workforce;

public static class WorkforceModule
{
    public static IServiceCollection AddWorkforceModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, WorkforceModelContributor>();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkforceModule(this IEndpointRouteBuilder app) => app;
}
