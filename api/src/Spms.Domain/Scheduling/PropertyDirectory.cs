namespace Spms.Domain.Scheduling;

/// <summary>
/// Property reference data. A stub until the property master lands, but it is
/// business reference data, so it belongs beside the service catalogue rather
/// than in the API project — it was previously at the bottom of an endpoint
/// file, which is the one place nobody looks for a time zone.
/// </summary>
public sealed record PropertyProfile(string PropertyId, string TimeZoneId, int OpenMinute, int CloseMinute);

public static class PropertyDirectory
{
    private static readonly PropertyProfile[] All =
    [
        // 09:00–17:00 local.
        new("prop-riverside", "America/New_York", 9 * 60, 17 * 60),
    ];

    public static PropertyProfile For(string propertyId) =>
        All.FirstOrDefault(p => p.PropertyId == propertyId)
        ?? new PropertyProfile(propertyId, "UTC", 9 * 60, 17 * 60);
}
