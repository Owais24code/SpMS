using Spms.Domain.Scheduling;

namespace Spms.Domain.Abstractions;

/*
 * Reference-data ports.
 *
 * The service catalogue, the qualification matrix, the buffer policy and the
 * property profile were static classes with hard-coded arrays. That made the
 * code the owner of business reference data, so a new hire could not be given
 * a qualification and a property could not change its turnover buffer without
 * a deployment — while the specification says both are configurable per
 * property and service. They are tables now, and these are the ports.
 */

public interface IServiceCatalog
{
    Task<CatalogService?> FindAsync(string tenantId, string serviceId, CancellationToken ct = default);
    Task<IReadOnlyList<CatalogService>> ListAsync(string tenantId, CancellationToken ct = default);
}

public interface IQualificationRegister
{
    /// <summary>
    /// Fails closed. A provider with no qualification row is NOT qualified:
    /// treating an unknown provider as qualified inverted a licensing rule for
    /// every new hire and every typo.
    /// </summary>
    Task<bool> IsQualifiedAsync(
        string tenantId, string propertyId, string providerId, string serviceId,
        DateTimeOffset asOfUtc, CancellationToken ct = default);

    /// <summary>
    /// Whether the provider exists at this property at all, so the refusal can
    /// say "not qualified" rather than "no record" — different conflicts with
    /// different resolutions.
    /// </summary>
    Task<bool> IsKnownAsync(
        string tenantId, string propertyId, string providerId, CancellationToken ct = default);
}

public interface IPropertyDirectory
{
    Task<PropertyProfile?> FindAsync(string tenantId, string propertyId, CancellationToken ct = default);
}

/// <summary>
/// A property's operational profile, including its buffers. One read rather
/// than four, because the conflict scan needs all of it on every preflight.
/// </summary>
public sealed record PropertyProfile(
    string PropertyId,
    string TimeZoneId,
    int OpenMinute,
    int CloseMinute,
    BufferPolicy DefaultBuffers,
    IReadOnlyDictionary<string, BufferPolicy> BuffersByService)
{
    public BufferPolicy BuffersFor(string serviceId) =>
        BuffersByService.TryGetValue(serviceId, out var b) ? b : DefaultBuffers;
}
