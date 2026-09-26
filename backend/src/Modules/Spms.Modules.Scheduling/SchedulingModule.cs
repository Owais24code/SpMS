using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Spms.SharedKernel;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Scheduling.Endpoints;
using Spms.Modules.Scheduling.Infrastructure;
using Spms.Modules.Scheduling.Operations;
using Spms.Persistence;

namespace Spms.Modules.Scheduling;

public static class SchedulingModule
{
    public static IServiceCollection AddSchedulingModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, SchedulingModelContributor>();

        // Scoped: they hold the request's DbContext and therefore its transaction.
        services.AddScoped<IAppointmentRepository, EfAppointmentRepository>();
        services.AddScoped<IPreflightStore, EfPreflightStore>();
        services.AddScoped<IServiceCatalog, EfServiceCatalog>();
        services.AddScoped<IQualificationRegister, EfQualificationRegister>();
        services.AddScoped<IPropertyDirectory, EfPropertyDirectory>();
        services.AddScoped<IGuestDirectory, EfGuestDirectory>();
        services.AddScoped<IResourceCalendar, EfResourceCalendar>();
        services.AddScoped<ISchedulingEffects, EfSchedulingEffects>();
        services.TryAddSingleton(new SchedulingOptions());
        services.AddScoped<SchedulingService>();
        services.AddScoped<AppointmentAccess>();
        services.AddScoped<VisitService>();
        services.AddScoped<WaitlistService>();
        services.AddScoped<TurnaroundService>();
        services.AddScoped<IGuestDataContributor, SchedulingGuestData>();

        services.AddScoped<IPropertyJob, HoldExpiryJob>();
        services.AddScoped<IPropertyJob, PreflightExpiryJob>();
        services.AddScoped<IPropertyJob, WaitlistExpiryJob>();
        return services;
    }

    public static IEndpointRouteBuilder MapSchedulingModule(this IEndpointRouteBuilder app)
    {
        app.MapAppointments();
        app.MapScheduling();
        app.MapOperations();
        app.MapKiosk();
        return app;
    }
}
