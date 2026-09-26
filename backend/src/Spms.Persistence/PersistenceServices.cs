using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Spms.SharedKernel;

namespace Spms.Persistence;

public static class PersistenceServices
{
    /// <summary>
    /// The scoped context with the tenancy interceptors, the unit of work and
    /// the shared data source. Modules add their <see cref="IModelContributor"/>.
    /// </summary>
    public static IServiceCollection AddSpmsPersistence(
        this IServiceCollection services, string connectionString, Action<PersistenceOptions>? configure = null)
    {
        var options = new PersistenceOptions();
        configure?.Invoke(options);
        services.AddSingleton(options);

        // One data source per process: it owns the pool. The idempotency store
        // opens its own connections from it, outside the request transaction.
        services.TryAddSingleton(_ => new NpgsqlDataSourceBuilder(connectionString).Build());

        services.TryAddScoped<ExecutionScope>();
        services.AddScoped<ScopeTransactionInterceptor>();
        services.AddScoped<StampingInterceptor>();
        services.AddSingleton<UnscopedCommandGuard>();

        services.AddDbContext<SpmsDbContext>((sp, o) =>
        {
            o.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(),
                npg => npg.MigrationsHistoryTable(DatabaseBootstrapper.HistoryTable, DatabaseBootstrapper.HistorySchema));
            o.ReplaceService<IModelCacheKeyFactory, ContributorModelCacheKeyFactory>();
            o.AddInterceptors(
                sp.GetRequiredService<ScopeTransactionInterceptor>(),
                sp.GetRequiredService<StampingInterceptor>(),
                sp.GetRequiredService<UnscopedCommandGuard>());
        });

        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        return services;
    }
}
