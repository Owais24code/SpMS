using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Messaging.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Messaging;

public static class MessagingModule
{
    public static IServiceCollection AddMessagingModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, MessagingModelContributor>();
        services.AddSingleton<IMessageSender, SimulatedMessageSender>();
        services.AddScoped<MessagingService>();
        services.AddScoped<IPropertyJob, ReminderJob>();
        services.AddScoped<IPropertyJob, DispatchJob>();
        return services;
    }

    public static IEndpointRouteBuilder MapMessagingModule(this IEndpointRouteBuilder app) => app.MapMessaging();
}
