using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Commerce.Application;
using Spms.Modules.Commerce.Data;
using Spms.Modules.Commerce.Endpoints;
using Spms.Modules.Commerce.Payments;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Commerce;

public static class CommerceModule
{
    public static IServiceCollection AddCommerceModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, CommerceModelContributor>();
        services.AddSingleton<IPaymentGateway, SimulatedGateway>();
        services.AddScoped<PaymentOwnership>();
        services.AddScoped<CommerceService>();
        services.AddScoped<RefundService>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<IPropertyJob, AmbiguousPaymentJob>();
        services.AddScoped<IPropertyJob, CartExpiryJob>();
        services.AddScoped<ISchedulingObserver, DepositForfeitObserver>();
        services.AddScoped<IDepositStatus, DepositStatus>();
        services.AddScoped<IGuestDataContributor, CommerceGuestData>();
        return services;
    }

    public static IEndpointRouteBuilder MapCommerceModule(this IEndpointRouteBuilder app)
    {
        app.MapCommerce();
        return app;
    }
}
