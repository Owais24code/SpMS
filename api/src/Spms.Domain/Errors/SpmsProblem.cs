namespace Spms.Domain.Errors;

/// <summary>
/// The problem+json catalogue from technical/docs/API_Error_Catalog.md.
///
/// These codes are part of the public contract: they appear in client logic,
/// audit records and acceptance evidence. Append only — never renamed, never
/// reused for a different meaning.
/// </summary>
public sealed record SpmsProblem(
    string Code,
    int Status,
    string Title,
    bool Retryable)
{
    public static readonly SpmsProblem ValidationFailed =
        new("VALIDATION_FAILED", 422, "One or more fields are invalid", false);

    public static readonly SpmsProblem AuthenticationRequired =
        new("AUTHENTICATION_REQUIRED", 401, "Authentication is required", false);

    public static readonly SpmsProblem AuthorizationDenied =
        new("AUTHORIZATION_DENIED", 403, "Your role does not permit this", false);

    public static readonly SpmsProblem NotFound =
        new("NOT_FOUND", 404, "No such record", false);

    /// <summary>Same key, different body. The original is NOT replayed.</summary>
    public static readonly SpmsProblem IdempotencyMismatch =
        new("IDEMPOTENCY_MISMATCH", 409, "This idempotency key was used with a different request", false);

    /// <summary>412 with the current version so the caller can merge.</summary>
    public static readonly SpmsProblem StaleVersion =
        new("STALE_VERSION", 412, "The record changed since you read it", false);

    /// <summary>Physically impossible. No role overrides this.</summary>
    public static readonly SpmsProblem HardConflict =
        new("HARD_CONFLICT", 409, "This change is not possible", false);

    public static readonly SpmsProblem SoftConflictApprovalRequired =
        new("SOFT_CONFLICT_APPROVAL_REQUIRED", 409, "This change needs an override reason", false);

    public static readonly SpmsProblem PreflightExpired =
        new("PREFLIGHT_EXPIRED", 409, "The preflight token is no longer valid", true);

    public static readonly SpmsProblem OwnershipAmbiguous =
        new("OWNERSHIP_AMBIGUOUS", 503, "No single owning system for this capability", true);

    public static readonly SpmsProblem DependencyTimeout =
        new("DEPENDENCY_TIMEOUT", 503, "A connected system did not respond", true);

    /// <summary>
    /// 202, not an error (BR-014). The request may or may not have taken
    /// effect. The caller must query the original by idempotency key and must
    /// never blindly retry.
    /// </summary>
    public static readonly SpmsProblem PaymentOutcomeAmbiguous =
        new("PAYMENT_OUTCOME_AMBIGUOUS", 202, "The payment outcome is not yet known", false);

    public static readonly SpmsProblem RateLimited =
        new("RATE_LIMITED", 429, "Too many requests", true);

    public static readonly SpmsProblem OfflineActionBlocked =
        new("OFFLINE_ACTION_BLOCKED", 503, "This action is not available in the current operating mode", true);
}
