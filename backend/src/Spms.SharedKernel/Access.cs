namespace Spms.SharedKernel;

/// <summary>
/// Fine-grained authorization: may this principal do this to this object?
/// Answered by OpenFGA (authorization/model.fga).
///
/// Fails CLOSED: when the decider cannot be reached it throws
/// <see cref="AccessUnavailableException"/> and the API answers 503 — never a
/// silent allow. Relationships that live on the row being handled (the
/// appointment's property, guest and provider) are passed as contextual tuples
/// so a reassign needs no tuple write.
/// </summary>
public interface IAccessDecider
{
    Task<AccessDecision> CheckAsync(AccessCheck check, CancellationToken ct = default);
}

public sealed record AccessCheck(
    string User,
    string Relation,
    string Object,
    IReadOnlyList<FgaTuple>? Contextual = null,
    IReadOnlyDictionary<string, object>? Context = null,
    /// <summary>HIGHER_CONSISTENCY: for revocation-sensitive checks.</summary>
    bool Strong = false);

public sealed record FgaTuple(string User, string Relation, string Object);

public sealed record AccessDecision(bool Allowed, string? ModelId);

public sealed class AccessUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>The object and user naming used in the OpenFGA model.</summary>
public static class Fga
{
    public static string User(Guid principalId) => $"user:{principalId}";
    public static string Tenant(Guid id) => $"tenant:{id}";
    public static string Property(Guid id) => $"property:{id}";
    public static string Guest(Guid id) => $"guest:{id}";
    public static string Appointment(Guid id) => $"appointment:{id}";
    public static string Device(Guid id) => $"device:{id}";
    public static string StaffRecord(Guid staffPrincipalId) => $"staff_record:{staffPrincipalId}";
    public static string IntakeSubmission(Guid id) => $"intake_submission:{id}";
    public static string TreatmentNote(Guid id) => $"treatment_note:{id}";

    public static string Property(string id) => $"property:{id}";
    public static string Guest(string id) => $"guest:{id}";
    public static string Appointment(string id) => $"appointment:{id}";
}

/// <summary>
/// The coarse gate: which spa.* scopes each role carries
/// (technical/config/role_permissions.json, scopeMap). defaultEffect is deny.
/// A token may narrow these (its own spa.* scopes are intersected); it can
/// never widen them — roles live in SpMS, not in the token.
/// </summary>
public static class RoleScopes
{
    private static readonly Dictionary<string, string[]> Map = new(StringComparer.Ordinal)
    {
        ["provider"] = [SpaScopes.Read, SpaScopes.HealthRestricted, SpaScopes.Commerce],
        ["front_desk"] = [SpaScopes.Read, SpaScopes.Write, SpaScopes.GuestWrite, SpaScopes.Device, SpaScopes.Commerce],
        ["scheduler"] = [SpaScopes.Read, SpaScopes.Write, SpaScopes.Schedule],
        ["spa_manager"] = [SpaScopes.Read, SpaScopes.Write, SpaScopes.Schedule, SpaScopes.GuestWrite, SpaScopes.WorkforceRead,
                           SpaScopes.Messaging, SpaScopes.Device, SpaScopes.Inventory, SpaScopes.Commerce],
        ["hr_compliance"] = [SpaScopes.Read, SpaScopes.WorkforceRead],
        ["finance"] = [SpaScopes.Read, SpaScopes.Commerce, SpaScopes.Reconcile],
        ["platform_admin"] = [SpaScopes.Read, SpaScopes.Admin],
        ["configuration_approver"] = [SpaScopes.Read, SpaScopes.Admin],
        ["housekeeping"] = [SpaScopes.Read, SpaScopes.Inventory],
        ["inventory_manager"] = [SpaScopes.Read, SpaScopes.Inventory],
        ["marketing"] = [SpaScopes.Read, SpaScopes.Messaging],
        ["support"] = [SpaScopes.Read],
        ["operations_analyst"] = [SpaScopes.Read],
        ["executive"] = [SpaScopes.Read],
        ["release_manager"] = [SpaScopes.Read, SpaScopes.Admin],
        ["security_admin"] = [SpaScopes.Read, SpaScopes.Admin],
        ["integration_service"] = [SpaScopes.Read, SpaScopes.Write, SpaScopes.Schedule, SpaScopes.Commerce, SpaScopes.Inventory],
    };

    public static IReadOnlyCollection<string> Roles => Map.Keys;

    public static IReadOnlySet<string> For(IEnumerable<string> roles) =>
        roles.SelectMany(r => Map.TryGetValue(r, out var s) ? s : []).ToHashSet(StringComparer.Ordinal);

    /// <summary>Role scopes, narrowed by the token's own spa.* scopes when it carries any.</summary>
    public static IReadOnlySet<string> Effective(IEnumerable<string> roles, IEnumerable<string> tokenScopes)
    {
        var granted = For(roles);
        var fromToken = tokenScopes.Where(s => s.StartsWith("spa.", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        return fromToken.Count == 0 ? granted : granted.Where(fromToken.Contains).ToHashSet(StringComparer.Ordinal);
    }
}
