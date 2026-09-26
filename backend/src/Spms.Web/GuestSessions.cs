namespace Spms.Web;

/// <summary>
/// Mints the session a guest receives after redeeming a magic link. The link
/// proves control of a verified contact point; the session is what the guest
/// web then presents. Short-lived and purpose-limited (SEC-010/011).
/// </summary>
public interface IGuestSessionIssuer
{
    GuestSession Issue(GuestSessionClaims claims);
}

public sealed record GuestSessionClaims(
    Guid TenantId,
    Guid PrincipalId,
    Guid GuestId,
    Guid PropertyId,
    IReadOnlyList<Guid> PropertyIds,
    string DisplayName,
    string Purpose,
    string? ScopeEntityType,
    Guid? ScopeEntityId);

public sealed record GuestSession(string AccessToken, DateTimeOffset ExpiresUtc);
