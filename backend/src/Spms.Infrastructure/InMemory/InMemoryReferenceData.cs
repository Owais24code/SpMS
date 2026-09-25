using Spms.Domain.Abstractions;
using Spms.Domain.Scheduling;

namespace Spms.Infrastructure.InMemory;

/*
 * In-memory reference data and transaction boundary.
 *
 * These exist so the domain suite runs with no database — fast, and free in
 * CI. They are NOT a production path, and the gap between them and the
 * Postgres adapters is exactly where port drift hides, so both sets are run
 * against one shared conformance suite rather than trusted separately.
 */

/// <summary>
/// A no-op transaction boundary.
///
/// Honest about what it is: there is no atomicity here, so a failure part-way
/// through a commit path leaves earlier writes applied. That is acceptable in
/// a test double and unacceptable in production, which is the reason the
/// Postgres implementation exists and why the integration tests — not these —
/// are what prove the audit row and the appointment write land together.
/// </summary>
public sealed class NullUnitOfWork : IUnitOfWork
{
    public Task<ITransaction> BeginAsync(CancellationToken ct = default) =>
        Task.FromResult<ITransaction>(new Scope());

    private sealed class Scope : ITransaction
    {
        public Task CommitAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class InMemoryServiceCatalog : IServiceCatalog
{
    private readonly Dictionary<string, List<CatalogService>> _byTenant = new(StringComparer.Ordinal);

    public void Add(string tenantId, params CatalogService[] services)
    {
        if (!_byTenant.TryGetValue(tenantId, out var list)) _byTenant[tenantId] = list = [];
        list.AddRange(services);
    }

    public Task<CatalogService?> FindAsync(string tenantId, string serviceId, CancellationToken ct = default) =>
        Task.FromResult(_byTenant.TryGetValue(tenantId, out var l)
            ? l.FirstOrDefault(s => s.ServiceId == serviceId)
            : null);

    public Task<IReadOnlyList<CatalogService>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CatalogService>>(
            _byTenant.TryGetValue(tenantId, out var l) ? l.OrderBy(s => s.ServiceId).ToList() : []);
}

public sealed class InMemoryQualificationRegister : IQualificationRegister
{
    private readonly record struct Key(string Tenant, string Property, string Provider, string Service);

    private readonly Dictionary<Key, DateTimeOffset?> _grants = [];
    private readonly HashSet<(string Tenant, string Property, string Provider)> _known = [];
    private readonly HashSet<(string Tenant, string Property, string Provider)> _unassignable = [];

    public void Grant(string tenant, string property, string provider, string service, DateTimeOffset? expiresUtc = null)
    {
        _grants[new Key(tenant, property, provider, service)] = expiresUtc;
        _known.Add((tenant, property, provider));
    }

    /// <summary>Registers a provider with no qualification, so CON-003 can distinguish the two refusals.</summary>
    public void Know(string tenant, string property, string provider) => _known.Add((tenant, property, provider));

    public void Suspend(string tenant, string property, string provider)
    {
        _known.Add((tenant, property, provider));
        _unassignable.Add((tenant, property, provider));
    }

    public Task<bool> IsQualifiedAsync(
        string tenantId, string propertyId, string providerId, string serviceId,
        DateTimeOffset asOfUtc, CancellationToken ct = default)
    {
        if (_unassignable.Contains((tenantId, propertyId, providerId))) return Task.FromResult(false);
        if (!_grants.TryGetValue(new Key(tenantId, propertyId, providerId, serviceId), out var expires))
            return Task.FromResult(false);   // fails closed
        return Task.FromResult(expires is null || expires > asOfUtc);
    }

    public Task<bool> IsKnownAsync(string tenantId, string propertyId, string providerId, CancellationToken ct = default) =>
        Task.FromResult(_known.Contains((tenantId, propertyId, providerId)));
}

public sealed class InMemoryPropertyDirectory : IPropertyDirectory
{
    private readonly Dictionary<(string Tenant, string Property), PropertyProfile> _profiles = [];

    public void Add(string tenantId, PropertyProfile profile) => _profiles[(tenantId, profile.PropertyId)] = profile;

    public Task<PropertyProfile?> FindAsync(string tenantId, string propertyId, CancellationToken ct = default) =>
        Task.FromResult(_profiles.TryGetValue((tenantId, propertyId), out var p) ? p : null);
}
