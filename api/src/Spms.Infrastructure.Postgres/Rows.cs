using Spms.Domain.Scheduling;

namespace Spms.Infrastructure.Postgres.Rows;

/*
 * Persistence rows. One class per table, column-for-column, mutable, with no
 * behaviour — the whole point is that they carry no rules, so nothing here can
 * disagree with the domain about what is legal. The rules live in the
 * aggregate and in the database's own constraints.
 */

public sealed class AppointmentRow
{
    public string TenantId { get; set; } = string.Empty;
    public string PropertyId { get; set; } = string.Empty;
    public string AppointmentId { get; set; } = string.Empty;
    public string GuestId { get; set; } = string.Empty;
    public string ServiceId { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
    public string? ProviderId { get; set; }
    public string? RoomId { get; set; }
    public DateTimeOffset StartUtc { get; set; }
    /// <summary>Set by the database trigger; read-only from here.</summary>
    public DateTimeOffset EndUtc { get; set; }
    /// <summary>
    /// The status name as stored. A CHECK constraint restricts it to the nine
    /// legal values, so this being a string cannot let an invalid status into
    /// the table the room-overlap constraint filters on.
    /// </summary>
    public string Status { get; set; } = nameof(AppointmentStatus.Draft);
    public int RowVersion { get; set; }
    public string? ConfirmationNumber { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
    public DateTimeOffset CreatedUtc { get; set; }
    public DateTimeOffset UpdatedUtc { get; set; }
}

public sealed class AuditRow
{
    public long AuditId { get; set; }
    public DateTimeOffset AtUtc { get; set; }
    public string TenantId { get; set; } = string.Empty;
    public string PropertyId { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string SubjectType { get; set; } = string.Empty;
    public string SubjectId { get; set; } = string.Empty;
    public int SubjectVersion { get; set; }
    public string? BeforeHash { get; set; }
    public string? AfterHash { get; set; }
    public string[] ConflictCodes { get; set; } = [];
    public string? SelectedResolution { get; set; }
    public string? TargetStatus { get; set; }
    public string? Reason { get; set; }
    public string CorrelationId { get; set; } = string.Empty;
}

public sealed class IdempotencyRow
{
    public string TenantId { get; set; } = string.Empty;
    public string PropertyId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string Route { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public bool Completed { get; set; }
    public int StatusCode { get; set; }
    public string? ResponseJson { get; set; }
    public DateTimeOffset AtUtc { get; set; }
}

public sealed class PreflightRow
{
    public string TenantId { get; set; } = string.Empty;
    public string Token { get; set; } = string.Empty;
    public string PropertyId { get; set; } = string.Empty;
    public string AppointmentId { get; set; } = string.Empty;
    public DateTimeOffset ProposedStartUtc { get; set; }
    public DateTimeOffset ProposedEndUtc { get; set; }
    public string? ProposedProviderId { get; set; }
    public string? ProposedRoomId { get; set; }
    public int FromRowVersion { get; set; }
    public string ConflictsJson { get; set; } = "[]";
    public DateTimeOffset IssuedUtc { get; set; }
    public DateTimeOffset ExpiresUtc { get; set; }
}

public sealed class PropertyRow
{
    public string TenantId { get; set; } = string.Empty;
    public string PropertyId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = "UTC";
    public int OpenMinute { get; set; }
    public int CloseMinute { get; set; }
}

public sealed class ServiceRow
{
    public string TenantId { get; set; } = string.Empty;
    public string ServiceId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int DurationMinutes { get; set; }
}

public sealed class StaffRow
{
    public string TenantId { get; set; } = string.Empty;
    public string PropertyId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool Assignable { get; set; }
}

public sealed class StaffQualificationRow
{
    public string TenantId { get; set; } = string.Empty;
    public string PropertyId { get; set; } = string.Empty;
    public string ProviderId { get; set; } = string.Empty;
    public string ServiceId { get; set; } = string.Empty;
    public DateTimeOffset GrantedUtc { get; set; }
    public DateTimeOffset? ExpiresUtc { get; set; }
}

public sealed class BufferPolicyRow
{
    public string TenantId { get; set; } = string.Empty;
    public string PropertyId { get; set; } = string.Empty;
    public string? ServiceId { get; set; }
    public int RoomTurnoverMinutes { get; set; }
    public int ProviderTransitionMinutes { get; set; }
}

public sealed class GuestRow
{
    public string TenantId { get; set; } = string.Empty;
    public string GuestId { get; set; } = string.Empty;
    public string DisplayAlias { get; set; } = string.Empty;
}

public sealed class RoomRow
{
    public string TenantId { get; set; } = string.Empty;
    public string PropertyId { get; set; } = string.Empty;
    public string RoomId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
}
