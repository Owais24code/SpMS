namespace Spms.Domain.Scheduling;

public sealed record CatalogService(string ServiceId, string Name, int DurationMinutes);

/// <summary>
/// Stub catalogue. Replaced by the service master; kept in the domain so the
/// API is not the owner of business reference data.
/// </summary>
public static class ServiceCatalog
{
    private static readonly CatalogService[] All =
    [
        new("svc-deep", "Deep tissue 90", 90),
        new("svc-aroma", "Aromatherapy 60", 60),
        new("svc-facial", "Facial 45", 45),
        new("svc-hotstone", "Hot stone 60", 60),
        new("svc-swedish", "Swedish 60", 60),
        new("svc-peel", "Peel 30", 30),
    ];

    public static CatalogService? Find(string id) => All.FirstOrDefault(s => s.ServiceId == id);
    public static IReadOnlyList<CatalogService> Services => All;
}
