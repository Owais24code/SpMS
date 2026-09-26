using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Spms.SharedKernel;

namespace Spms.Persistence;

/// <summary>
/// The one EF Core context across all eleven schemas.
///
/// Each module contributes its own mapping (<see cref="IModelContributor"/>),
/// generated from database/model/ like the SQL is, so the context has no
/// hand-written table list to drift from the schema.
///
/// Tenancy, layer 2 of 3: every tenant table gets a query filter on the
/// execution scope's tenant (and properties, for property-scoped tables). A
/// new endpoint cannot forget to scope, because the scoping is not at the call
/// site. Layer 3 — row-level security — sits underneath and holds even when a
/// query deliberately calls IgnoreQueryFilters().
///
/// The schema is owned by the migrations (the reviewed SQL), never by
/// EnsureCreated.
/// </summary>
public sealed class SpmsDbContext : DbContext
{
    private readonly ExecutionScope _scope;
    private readonly IReadOnlyList<IModelContributor> _contributors;

    public SpmsDbContext(DbContextOptions<SpmsDbContext> options, ExecutionScope scope, IEnumerable<IModelContributor> contributors)
        : base(options)
    {
        _scope = scope;
        _contributors = contributors.OrderBy(c => c.Schema, StringComparer.Ordinal).ToList();
    }

    /// <summary>Read by the query filters. An unset scope matches nothing.</summary>
    internal Guid ScopeTenantId => _scope.TenantId ?? Guid.Empty;
    internal Guid[] ScopePropertyIds => _scope.PropertyIds as Guid[] ?? _scope.PropertyIds.ToArray();

    public ExecutionScope Scope => _scope;

    internal string ModelKey => string.Join(",", _contributors.Select(c => c.Schema));

    protected override void OnModelCreating(ModelBuilder b)
    {
        foreach (var c in _contributors) c.Configure(b);
        ApplyTenancyFilters(b);
    }

    private void ApplyTenancyFilters(ModelBuilder b)
    {
        var ctx = Expression.Constant(this);
        var tenant = Expression.Property(ctx, nameof(ScopeTenantId));
        var props = Expression.Property(ctx, nameof(ScopePropertyIds));
        var contains = typeof(Enumerable).GetMethods()
            .Single(m => m.Name == nameof(Enumerable.Contains) && m.GetParameters().Length == 2)
            .MakeGenericMethod(typeof(Guid));

        foreach (var et in b.Model.GetEntityTypes())
        {
            var clr = et.ClrType;
            if (!typeof(ITenantOwned).IsAssignableFrom(clr)) continue;

            var row = Expression.Parameter(clr, "row");
            Expression body = Expression.Equal(Expression.Property(row, nameof(ITenantOwned.TenantId)), tenant);

            if (typeof(IPropertyOwned).IsAssignableFrom(clr))
            {
                body = Expression.AndAlso(body,
                    Expression.Call(contains, props, Expression.Property(row, nameof(IPropertyOwned.PropertyId))));
            }
            else if (typeof(IOptionalPropertyOwned).IsAssignableFrom(clr))
            {
                var p = Expression.Property(row, nameof(IOptionalPropertyOwned.PropertyId));
                body = Expression.AndAlso(body, Expression.OrElse(
                    Expression.Equal(p, Expression.Constant(null, typeof(Guid?))),
                    Expression.Call(contains, props, Expression.Property(p, nameof(Nullable<Guid>.Value)))));
            }

            b.Entity(clr).HasQueryFilter(Expression.Lambda(body, row));
        }
    }
}

/// <summary>
/// The model is built once per distinct set of contributing modules. Without
/// this, a test context with three modules and the host's context with eleven
/// would share whichever model was built first.
/// </summary>
internal sealed class ContributorModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        context is SpmsDbContext s ? (typeof(SpmsDbContext), s.ModelKey, designTime) : (context.GetType(), designTime);
}
