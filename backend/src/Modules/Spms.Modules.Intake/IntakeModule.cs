using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Intake.Data;
using Spms.Persistence;

namespace Spms.Modules.Intake;

public static class IntakeModule
{
    public static IServiceCollection AddIntakeModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, IntakeModelContributor>();
        return services;
    }

    public static IEndpointRouteBuilder MapIntakeModule(this IEndpointRouteBuilder app) => app;
}
