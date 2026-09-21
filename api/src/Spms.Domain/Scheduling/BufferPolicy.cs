namespace Spms.Domain.Scheduling;

/// <summary>
/// Turnover and transition buffers — the most spec-relevant knob in CON-004,
/// so it gets its own file rather than sitting at the bottom of the service
/// catalogue where nobody would grep for it.
///
/// Spec baselines: 15 minutes room turnover, 10 minutes provider transition,
/// both configurable per property and service. Neither is configurable yet;
/// this default is the only source, which is why it is not called Default
/// anywhere the reader could mistake for "one of several".
/// </summary>
public sealed record BufferPolicy(int RoomTurnoverMinutes, int ProviderTransitionMinutes)
{
    public static readonly BufferPolicy Default = new(RoomTurnoverMinutes: 15, ProviderTransitionMinutes: 10);
}
