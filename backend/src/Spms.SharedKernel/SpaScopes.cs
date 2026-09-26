namespace Spms.SharedKernel;

/// <summary>
/// OAuth scopes exactly as they appear in technical/config/role_permissions.json.
/// defaultEffect is deny: anything not granted is refused.
/// </summary>
public static class SpaScopes
{
    public const string Read             = "spa.read";
    public const string Write            = "spa.write";
    public const string Schedule         = "spa.schedule";
    public const string GuestWrite       = "spa.guest.write";
    public const string HealthRestricted = "spa.health.restricted";
    public const string WorkforceRead    = "spa.workforce.read";
    public const string Commerce         = "spa.commerce";
    public const string Messaging        = "spa.messaging";
    public const string Device           = "spa.device";
    public const string Inventory        = "spa.inventory";
    public const string Reconcile        = "spa.reconcile";
    public const string Admin            = "spa.admin";

    /// <summary>A signed-in guest acting on their own affairs (magic-link session).</summary>
    public const string GuestSelf        = "spa.guest.self";
}
