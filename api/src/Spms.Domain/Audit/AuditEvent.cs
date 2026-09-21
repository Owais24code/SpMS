namespace Spms.Domain.Audit;

/// <summary>
/// An audit row. Written in the SAME transaction as the change it describes —
/// an audit trail that can diverge from the data is not an audit trail.
///
/// Before/after are hashes, not values: the register covers tables holding
/// restricted fields, and the audit store must not become a second copy of
/// them (API-004 — PII never in logs).
/// </summary>
public sealed record AuditEvent
{
    public required Guid AuditId { get; init; }
    public required DateTimeOffset OccurredAtUtc { get; init; }
    public required string TenantId { get; init; }
    public required string PropertyId { get; init; }
    public required string Actor { get; init; }
    public required string Action { get; init; }
    public required string SubjectType { get; init; }
    public required string SubjectId { get; init; }
    public required int SubjectVersion { get; init; }

    /// <summary>Why the actor was entitled to do this — required for restricted access.</summary>
    public required string Purpose { get; init; }

    public string? BeforeHash { get; init; }
    public string? AfterHash { get; init; }

    /// <summary>Conflict or problem code, when the action resolved one.</summary>
    public string? Code { get; init; }

    public required string CorrelationId { get; init; }
}

public interface IAuditSink
{
    void Write(AuditEvent e);
    IReadOnlyList<AuditEvent> Recent(int take = 50);
}
