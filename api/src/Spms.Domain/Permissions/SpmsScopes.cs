namespace Spms.Domain.Permissions;

/// <summary>
/// OAuth scopes, verbatim from technical/config/role_permissions.json.
/// defaultEffect is deny: a scope not granted is denied.
/// </summary>
public static class SpmsScopes
{
    public const string Read              = "spa.read";
    public const string Write             = "spa.write";
    public const string Schedule          = "spa.schedule";
    public const string GuestWrite        = "spa.guest.write";
    public const string HealthRestricted  = "spa.health.restricted";
    public const string WorkforceRead     = "spa.workforce.read";
    public const string Commerce          = "spa.commerce";
    public const string Messaging         = "spa.messaging";
    public const string Device            = "spa.device";
    public const string Inventory         = "spa.inventory";
    public const string Reconcile         = "spa.reconcile";
    public const string Admin             = "spa.admin";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
    {
        Read, Write, Schedule, GuestWrite, HealthRestricted, WorkforceRead,
        Commerce, Messaging, Device, Inventory, Reconcile, Admin,
    };
}

/// <summary>
/// Field paths the serializer must withhold unless scope AND relationship AND
/// purpose all hold (SEC-020). Hiding a column in the UI is not authorization,
/// so these are filtered here, on the way out.
/// </summary>
public static class RestrictedFields
{
    public const string IntakeResponses          = "intake.responses";
    public const string TreatmentNoteContent     = "treatment_note.content";
    public const string CredentialNumber         = "credential.number";
    public const string StaffDocumentObjectRef   = "staff_document.object_reference";
    public const string ScreeningAdjudication    = "background_screening.adjudication";
    public const string CommercePaymentToken     = "commerce.payment_token";
}
