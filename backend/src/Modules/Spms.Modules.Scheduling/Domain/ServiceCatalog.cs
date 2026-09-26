namespace Spms.Modules.Scheduling.Domain;

/// <summary>
/// A service as the scheduler needs it, with the property's effective price
/// (catalog.property_service overrides catalog.service). An appointment freezes
/// the duration and price it booked.
/// </summary>
public sealed record CatalogService(
    string ServiceId,
    string Name,
    int DurationMinutes,
    long PriceMinor = 0,
    string CurrencyCode = "USD",
    bool OnlineBookable = false,
    bool DepositRequired = false,
    bool RequiresIntake = false,
    string? Code = null);
