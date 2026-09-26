using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Reporting.Data;
using Spms.Persistence;

namespace Spms.Modules.Reporting;

public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        services.AddSingleton<IModelContributor, ReportingModelContributor>();
        return services;
    }

    public static IEndpointRouteBuilder MapReportingModule(this IEndpointRouteBuilder app) => app;
}
