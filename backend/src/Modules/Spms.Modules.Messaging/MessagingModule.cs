using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Messaging.Data;
using Spms.Persistence;

namespace Spms.Modules.Messaging;

public static class MessagingModule
{
    public static IServiceCollection AddMessagingModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, MessagingModelContributor>();
        return services;
    }

    public static IEndpointRouteBuilder MapMessagingModule(this IEndpointRouteBuilder app) => app;
}
