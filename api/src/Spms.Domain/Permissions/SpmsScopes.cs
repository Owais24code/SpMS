namespace Spms.Domain.Permissions;

/// <summary>
/// The complete permission catalogue, extracted verbatim from the SpMS UX
/// Field Specification v1.1 (UX-001 … UX-012).
///
/// These strings are the contract between the UI, the API and the identity
/// provider. They are never composed at runtime and never renamed without a
/// migration, because they also appear in audit records.
/// </summary>
public static class SpmsScopes
{
    // ---- Public / guest -------------------------------------------------
    /// <summary>Unauthenticated booking surfaces (UX-002).</summary>
    public const string Public = "public";

    /// <summary>A guest acting on their own appointments (UX-002, UX-003).</summary>
    public const string GuestOwn = "guest.own";

    // ---- Scheduling (UX-001) -------------------------------------------
    public const string SpaRead     = "spa.read";
    public const string SpaSchedule = "spa.schedule";

    /// <summary>
    /// Commits a SOFT conflict override. Never permits a hard physical
    /// conflict — that constraint is not role-gated because it is physical.
    /// </summary>
    public const string SpaOverride = "spa.override";

    // ---- Front desk (UX-004) -------------------------------------------
    public const string FrontDesk = "frontdesk";

    // ---- Provider and treatment (UX-005) --------------------------------
    public const string Provider       = "provider";
    public const string TreatmentWrite = "treatment.write";

    /// <summary>
    /// Minimum-necessary intake restrictions shown to the assigned provider.
    /// Purpose- and relationship-bound: holding the scope is not sufficient,
    /// the caller must also be assigned to the appointment.
    /// </summary>
    public const string HealthRestricted = "health.restricted";

    // ---- Commerce -------------------------------------------------------
    public const string CommerceOrder = "commerce.order";

    // ---- Staff compliance (UX-006) --------------------------------------
    public const string StaffRead          = "staff.read";
    public const string HrCompliance       = "hr.compliance";
    public const string HrRestricted       = "hr.restricted";
    public const string ScreeningRestricted = "screening.restricted";

    // ---- Inventory and readiness (UX-007) -------------------------------
    public const string Inventory    = "inventory";
    public const string Housekeeping = "housekeeping";

    // ---- Messaging (UX-008) ---------------------------------------------
    public const string MessagingAdmin = "messaging.admin";

    // ---- Devices and quiet notification (UX-004, UX-009) -----------------
    public const string Device       = "device";
    public const string DeviceAssign = "device.assign";
    public const string DeviceAlert  = "device.alert";
    public const string DeviceReturn = "device.return";

    // ---- Capability ownership (UX-010) -----------------------------------
    public const string ConfigPropose  = "config.propose";
    public const string ConfigValidate = "config.validate";

    /// <summary>Four-eyes approval. The proposer can never self-approve.</summary>
    public const string ConfigApprove  = "config.approve";
    public const string ConfigRollback = "config.rollback";

    // ---- Reconciliation (UX-011) -----------------------------------------
    public const string Reconcile        = "reconcile";
    public const string ReconcileReplay  = "reconcile.replay";
    public const string ReconcileResolve = "reconcile.resolve";
    public const string FinanceRestricted = "finance.restricted";

    // ---- Reports (UX-012) -------------------------------------------------
    public const string Reports            = "reports";
    public const string ReportsOperational = "reports.operational";
    public const string ReportsFinancial   = "reports.financial";
    public const string ReportsCompliance  = "reports.compliance";
    public const string ReportsExport      = "reports.export";

    /// <summary>
    /// Scopes that carry restricted personal data. A serializer must never
    /// emit fields governed by these on the strength of the scope alone —
    /// relationship and declared purpose are checked as well.
    /// </summary>
    public static readonly IReadOnlySet<string> Restricted = new HashSet<string>
    {
        HealthRestricted,
        HrRestricted,
        ScreeningRestricted,
        FinanceRestricted,
    };
}
