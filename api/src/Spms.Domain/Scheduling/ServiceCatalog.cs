namespace Spms.Domain.Scheduling;

/// <summary>
/// A service as the scheduler needs it. Reference data now lives in the
/// service table and reaches the domain through IServiceCatalog; this is the
/// value that port returns.
/// </summary>
public sealed record CatalogService(string ServiceId, string Name, int DurationMinutes);
