using Microsoft.AspNetCore.Routing;
using Spms.Modules.Catalog;
using Spms.Modules.Commerce;
using Spms.Modules.Core;
using Spms.Modules.Guest;
using Spms.Modules.Intake;
using Spms.Modules.Inventory;
using Spms.Modules.Messaging;
using Spms.Modules.Reporting;
using Spms.Modules.Resources;
using Spms.Modules.Scheduling;
using Spms.Modules.Workforce;
using Spms.Persistence;

namespace Spms.Host;

/// <summary>Every module, in DAG order. The one list of what the process is made of.</summary>
public static class SpmsModules
{
    public static IServiceCollection AddSpmsModules(this IServiceCollection services) => services
        .AddCoreModule()
        .AddCatalogModule()
        .AddResourcesModule()
        .AddWorkforceModule()
        .AddGuestModule()
        .AddSchedulingModule()
        .AddIntakeModule()
        .AddInventoryModule()
        .AddCommerceModule()
        .AddMessagingModule()
        .AddReportingModule();

    public static IEndpointRouteBuilder MapSpmsModules(this IEndpointRouteBuilder app)
    {
        app.MapCoreModule();
        app.MapCatalogModule();
        app.MapResourcesModule();
        app.MapWorkforceModule();
        app.MapGuestModule();
        app.MapSchedulingModule();
        app.MapIntakeModule();
        app.MapInventoryModule();
        app.MapCommerceModule();
        app.MapMessagingModule();
        app.MapReportingModule();
        return app;
    }

    /// <summary>The model contributors without a container: design-time tooling and the migrator.</summary>
    public static IReadOnlyList<IModelContributor> Contributors()
    {
        var services = new ServiceCollection();
        services.AddSpmsModules();
        using var sp = services.BuildServiceProvider();
        return sp.GetServices<IModelContributor>().ToList();
    }
}
