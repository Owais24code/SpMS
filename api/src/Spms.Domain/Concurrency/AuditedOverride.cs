namespace Spms.Domain.Concurrency;

/// <summary>
/// A recorded override of a soft conflict.
///
/// Required before commit and always audited (UX-001). A hard conflict can
/// never produce one of these — the domain refuses to construct it.
/// </summary>
public sealed record AuditedOverride
{
    public required string ConflictCode { get; init; }
    public required string ReasonCode { get; init; }
    public string? Comment { get; init; }
    public required Guid ActorId { get; init; }
    public required DateTimeOffset RecordedAtUtc { get; init; }
    public required string CorrelationId { get; init; }
}
