using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Workforce;

public static class WorkforceModule
{
    public static IServiceCollection AddWorkforceModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, WorkforceModelContributor>();
        services.AddScoped<WorkforceService>();
        services.AddScoped<IPropertyJob, CredentialExpiryJob>();
        return services;
    }

    public static IEndpointRouteBuilder MapWorkforceModule(this IEndpointRouteBuilder app) => app.MapWorkforce();
}
