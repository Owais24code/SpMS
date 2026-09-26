namespace Spms.Modules.Scheduling.Domain;

/// <summary>
/// Turnover and transition buffers — the most spec-relevant knob in CON-004,
/// so it gets its own file rather than sitting at the bottom of the service
/// catalogue where nobody would grep for it.
///
/// Spec baselines: 15 minutes room turnover, 10 minutes provider transition,
/// both configurable per property and service. Both now come from the
/// buffer_policy table, keyed by property with an optional per-service
/// override; this type is the value, not the source.
/// </summary>
public sealed record BufferPolicy(int RoomTurnoverMinutes, int ProviderTransitionMinutes)
{
    /// <summary>
    /// Used only when a property has no buffer_policy row at all. It is named
    /// Fallback rather than Default so nobody reads it as "the configured
    /// value" — the configured value lives in the database, and a property
    /// reaching this has a data gap worth noticing.
    /// </summary>
    public static readonly BufferPolicy Fallback = new(RoomTurnoverMinutes: 15, ProviderTransitionMinutes: 10);
}
