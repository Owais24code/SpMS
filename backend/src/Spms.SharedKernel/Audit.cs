using System.Security.Cryptography;
using System.Text;

namespace Spms.SharedKernel;

/// <summary>
/// The write side of core.audit_event, as every module sees it.
///
/// Recorded on the request's DbContext, so the row commits in the same
/// transaction as the change it describes; a failure between the two can no
/// longer leave a change with no trail.
/// </summary>
public interface IAuditSink
{
    Task RecordAsync(AuditEntry entry, CancellationToken ct = default);
}

/// <summary>
/// Append-only audit record.
///
/// BeforeHash/AfterHash are SHA-256 over a canonical rendering of the state
/// rather than the values themselves, so the trail cannot become a second
/// copy of restricted data while still proving what changed (§54.3).
/// </summary>
public sealed record AuditEntry(
    string Action,
    string EntityType,
    string EntityId,
    int? EntityVersion = null,
    string? Purpose = null,
    string? FromStatus = null,
    string? ToStatus = null,
    string? BeforeHash = null,
    string? AfterHash = null,
    IReadOnlyList<string>? ConflictCodes = null,
    string? ReasonCode = null,
    string? ReasonText = null,
    IReadOnlyList<string>? ChangedFields = null,
    /// <summary>Non-restricted before/after projections. Never pass a restricted column here.</summary>
    object? BeforeData = null,
    object? AfterData = null,
    AuthorizationDecision? Authorization = null,
    Guid? OnBehalfOfPrincipalId = null,
    /// <summary>Overrides the scope's current property (tenant-wide entities pass null explicitly via TenantWide).</summary>
    Guid? PropertyId = null,
    bool TenantWide = false);

/// <summary>An OpenFGA decision worth keeping with the audit row (sensitive reads).</summary>
public sealed record AuthorizationDecision(string Relation, string Object, string? ModelId, bool Allowed);

public static class Hashing
{
    /// <summary>Lower-case hex SHA-256, the char(64) format of the *_hash columns.</summary>
    public static string Sha256Hex(string canonical) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();

    public static string Sha256Hex(ReadOnlySpan<byte> data) =>
        Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();
}
