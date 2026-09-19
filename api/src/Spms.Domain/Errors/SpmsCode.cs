namespace Spms.Domain.Errors;

/// <summary>
/// Stable error and conflict codes.
///
/// The UX spec requires a stable code on every validation failure and every
/// conflict, and requires analytics to record the CODE and never the field
/// value. These strings are therefore part of the public contract: they are
/// safe to log, safe to put in telemetry, and safe to show to a user
/// alongside localized copy.
///
/// Format: SPMS-&lt;area&gt;-&lt;nnn&gt;. Codes are append-only — never reused.
/// </summary>
public static class SpmsCode
{
    // ---- Concurrency (all surfaces) -------------------------------------
    /// <summary>The record changed since it was read. Unsaved work is preserved.</summary>
    public const string StaleVersion = "SPMS-CONC-001";
    public const string VersionMissing = "SPMS-CONC-002";

    // ---- Scheduling conflicts (UX-001) ----------------------------------
    /// <summary>Provider already booked in the requested window. Soft — overridable.</summary>
    public const string ProviderDoubleBooked = "SPMS-SCHED-001";

    /// <summary>Room or resource already occupied. HARD — not overridable by anyone.</summary>
    public const string ResourceDoubleBooked = "SPMS-SCHED-002";

    /// <summary>Provider lacks a qualification the service requires. Soft.</summary>
    public const string ProviderNotQualified = "SPMS-SCHED-003";

    /// <summary>Resource is not compatible with the service. HARD.</summary>
    public const string ResourceIncompatible = "SPMS-SCHED-004";

    /// <summary>Room has not reached Ready state. HARD.</summary>
    public const string RoomNotReady = "SPMS-SCHED-005";

    /// <summary>Requested slot falls outside the property's booking horizon.</summary>
    public const string OutsideBookingHorizon = "SPMS-SCHED-006";

    /// <summary>An override was attempted without a reason, or without the scope.</summary>
    public const string OverrideNotPermitted = "SPMS-SCHED-007";

    // ---- Payment and commerce (UX-002, UX-011) ---------------------------
    /// <summary>
    /// Outcome unknown. NOT a failure: the original must be queried by
    /// idempotency key. Never blind-retry on this code.
    /// </summary>
    public const string PaymentAmbiguous = "SPMS-PAY-001";
    public const string PaymentDeclined  = "SPMS-PAY-002";
    public const string DepositRequired  = "SPMS-PAY-003";

    // ---- Authorization ---------------------------------------------------
    public const string PermissionDenied = "SPMS-AUTH-001";

    /// <summary>
    /// Caller holds the scope but lacks the relationship or declared purpose.
    /// Distinguished from PermissionDenied so audit can tell them apart.
    /// </summary>
    public const string RelationshipRequired = "SPMS-AUTH-002";
    public const string SessionExpired       = "SPMS-AUTH-003";

    // ---- Assignability (UX-006) -------------------------------------------
    /// <summary>
    /// Staff member cannot be assigned. The reason is deliberately NOT in the
    /// code — explaining the block must not leak restricted compliance detail.
    /// </summary>
    public const string StaffNotAssignable = "SPMS-STAFF-001";

    // ---- Configuration ownership (UX-010) ---------------------------------
    public const string OwnerOverlap       = "SPMS-CFG-001";
    public const string PreflightFailed    = "SPMS-CFG-002";
    public const string SelfApprovalDenied = "SPMS-CFG-003";

    // ---- Dependencies ------------------------------------------------------
    public const string DependencyTimeout     = "SPMS-DEP-001";
    public const string DependencyUnavailable = "SPMS-DEP-002";
}

/// <summary>Whether a scheduling conflict may be overridden at all.</summary>
public enum ConflictSeverity
{
    /// <summary>Overridable by an authorized role with a recorded reason.</summary>
    Soft,

    /// <summary>
    /// A physical impossibility. No role, reason or approval can commit it —
    /// the UI must present it as blocked rather than as a confirmation.
    /// </summary>
    Hard,
}
