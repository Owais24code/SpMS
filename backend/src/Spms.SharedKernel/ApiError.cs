namespace Spms.SharedKernel;

/// <summary>
/// The problem+json catalogue from technical/docs/API_Error_Catalog.md.
///
/// These codes reach clients, logs and acceptance evidence, so they are
/// append-only and never renamed. The status is part of the contract too:
/// STALE_VERSION is 412 rather than 409, and PAYMENT_OUTCOME_AMBIGUOUS is a
/// 202 because it is not a failure.
/// </summary>
public sealed record ApiError(string Code, int Status, string Title)
{
    public static readonly ApiError ValidationFailed        = new("VALIDATION_FAILED", 422, "The request was not valid");
    public static readonly ApiError AuthenticationRequired  = new("AUTHENTICATION_REQUIRED", 401, "Authentication is required");
    public static readonly ApiError AuthorizationDenied     = new("AUTHORIZATION_DENIED", 403, "Your role does not permit this");
    public static readonly ApiError NotFound                = new("NOT_FOUND", 404, "No such record");
    public static readonly ApiError IdempotencyMismatch     = new("IDEMPOTENCY_MISMATCH", 409, "That idempotency key was used with a different request");
    public static readonly ApiError StaleVersion            = new("STALE_VERSION", 412, "The record changed since you read it");
    public static readonly ApiError HardConflict            = new("HARD_CONFLICT", 409, "This cannot be committed");
    public static readonly ApiError SoftConflictApproval    = new("SOFT_CONFLICT_APPROVAL_REQUIRED", 409, "An override reason is required");
    public static readonly ApiError PreflightExpired        = new("PREFLIGHT_EXPIRED", 409, "The preflight token is no longer valid");
    public static readonly ApiError OwnershipAmbiguous      = new("OWNERSHIP_AMBIGUOUS", 503, "No single owning system is active");
    public static readonly ApiError DependencyTimeout       = new("DEPENDENCY_TIMEOUT", 503, "A dependency did not respond");
    public static readonly ApiError PaymentOutcomeAmbiguous = new("PAYMENT_OUTCOME_AMBIGUOUS", 202, "The payment outcome is not yet known");
    public static readonly ApiError RateLimited             = new("RATE_LIMITED", 429, "Too many requests");
    /// <summary>A defect on our side. Never a dependency timeout — that mislabels our own bugs as infrastructure flakiness.</summary>
    public static readonly ApiError InternalError           = new("INTERNAL_ERROR", 500, "The request could not be completed");
    public static readonly ApiError OfflineActionBlocked    = new("OFFLINE_ACTION_BLOCKED", 503, "Not permitted in the current operating mode");
}
