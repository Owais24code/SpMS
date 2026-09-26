using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spms.Modules.Intake.Application;
using Spms.Modules.Intake.Data;
using Spms.Modules.Intake.Endpoints;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Intake;

public static class IntakeModule
{
    public static IServiceCollection AddIntakeModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, IntakeModelContributor>();
        services.TryAddSingleton<EnvelopeCipher>();
        services.AddScoped<IntakeRole>();
        services.AddScoped<IntakeService>();
        services.AddScoped<TreatmentNoteService>();
        services.AddScoped<FormService>();
        services.AddScoped<IGuestDataContributor, IntakeGuestData>();
        return services;
    }

    public static IEndpointRouteBuilder MapIntakeModule(this IEndpointRouteBuilder app)
    {
        app.MapIntake();
        return app;
    }
}
