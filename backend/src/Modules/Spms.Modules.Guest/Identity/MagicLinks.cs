using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Spms.Modules.Core.Data;
using Spms.Modules.Guest.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Guest.Identity;

/// <summary>
/// Hands a magic link to the channel that reaches the guest. Messaging
/// (scheduled_message, consent, quiet hours) implements it; until a
/// provider is configured the logging sender writes the link to the log in
/// Development and refuses elsewhere.
/// </summary>
public interface IGuestLinkSender
{
    Task SendAsync(GuestLinkDelivery delivery, CancellationToken ct);
}

public sealed record GuestLinkDelivery(
    Guid TenantId, Guid GuestId, Guid ContactPointId, string ContactType, string Purpose, string Url, DateTimeOffset ExpiresUtc);

public sealed class LoggingGuestLinkSender(ILogger<LoggingGuestLinkSender> logger, GuestLinkOptions options) : IGuestLinkSender
{
    public Task SendAsync(GuestLinkDelivery d, CancellationToken ct)
    {
        if (!options.LogLinks)
            throw new InvalidOperationException("No guest link sender is configured for this environment.");
        logger.LogWarning("Development magic link for guest {Guest} ({Purpose}): {Url}", d.GuestId, d.Purpose, d.Url);
        return Task.CompletedTask;
    }
}

public sealed class GuestLinkOptions
{
    /// <summary>Where the guest web is served; the link is {BaseUrl}/g/{token}.</summary>
    public string BaseUrl { get; set; } = "http://localhost:4200";
    public int DefaultMinutes { get; set; } = 15;
    public int MaxMinutes { get; set; } = 60;
    /// <summary>Development only: log links and return them in the issuing response.</summary>
    public bool LogLinks { get; set; }
}

public static class MagicLinkPurposes
{
    public const string SignIn = "SignIn";
    public const string ManageBooking = "ManageBooking";
    public const string CompleteIntake = "CompleteIntake";
    public const string Pay = "Pay";
    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal) { SignIn, ManageBooking, CompleteIntake, Pay };
}

/// <summary>
/// SEC-010/011 magic links: signed-in-by-possession, short-lived, single use,
/// purpose-limited. Only the SHA-256 of the token is stored; redemption goes
/// through guest.resolve_magic_link, which consumes it atomically.
/// </summary>
public sealed class MagicLinkService(
    SpmsDbContext db, IAuditSink audit, IOutbox outbox, IGuestLinkSender sender, GuestLinkOptions options, IClock clock)
{
    public enum IssueOutcome { Issued, UnknownGuest, UnknownContactPoint, UnverifiedContactPoint, BadPurpose }

    public sealed record IssueResult(IssueOutcome Outcome, Guid? LinkId = null, DateTimeOffset? ExpiresUtc = null, string? DevToken = null);

    public async Task<IssueResult> IssueAsync(Guid guestId, Guid contactPointId, string purpose,
        string? scopeEntityType, Guid? scopeEntityId, int? minutes, CancellationToken ct)
    {
        if (!MagicLinkPurposes.All.Contains(purpose)) return new IssueResult(IssueOutcome.BadPurpose);

        var guest = await db.Set<GuestRow>().SingleOrDefaultAsync(g => g.GuestId == guestId, ct);
        if (guest is null || guest.Status is not (GuestStatuses.Active or GuestStatuses.Restricted))
            return new IssueResult(IssueOutcome.UnknownGuest);

        var contact = await db.Set<GuestContactPointRow>().AsNoTracking()
            .SingleOrDefaultAsync(c => c.GuestContactPointId == contactPointId && c.GuestId == guestId, ct);
        if (contact is null || contact.Status != GuestContactPointStatuses.Active) return new IssueResult(IssueOutcome.UnknownContactPoint);
        // SEC-011: a link goes only to a contact method the guest has proved they control.
        if (contact.VerifiedAt is null) return new IssueResult(IssueOutcome.UnverifiedContactPoint);

        var principalId = await EnsurePrincipalAsync(guest, ct);

        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var now = clock.UtcNow;
        var expires = now.AddMinutes(Math.Clamp(minutes ?? options.DefaultMinutes, 1, options.MaxMinutes));
        var link = new GuestMagicLinkRow
        {
            GuestId = guestId,
            PrincipalId = principalId,
            GuestContactPointId = contactPointId,
            TokenHash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token)),
            Purpose = purpose,
            ScopeEntityType = scopeEntityId is null ? null : scopeEntityType,
            ScopeEntityId = scopeEntityType is null ? null : scopeEntityId,
            IssuedAt = now,
            ExpiresAt = expires,
        };
        db.Add(link);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry("guest.magic_link.issue", "guest", guestId.ToString(),
            Purpose: purpose, AfterData: new { contactType = contact.ContactType, expiresUtc = expires }, TenantWide: true), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.MagicLinkIssued, "guest", guestId, guest.Version,
            new { magicLinkId = link.GuestMagicLinkId, purpose, contactPointId, expiresUtc = expires }, TenantWide: true));

        await sender.SendAsync(new GuestLinkDelivery(guest.TenantId, guestId, contactPointId, contact.ContactType, purpose,
            $"{options.BaseUrl.TrimEnd('/')}/g/{token}", expires), ct);

        return new IssueResult(IssueOutcome.Issued, link.GuestMagicLinkId, expires, options.LogLinks ? token : null);
    }

    /// <summary>
    /// A guest who has never signed in has no principal yet. One is created on
    /// the first link, owned by the guest (OpenFGA owner tuple via the outbox).
    /// </summary>
    private async Task<Guid> EnsurePrincipalAsync(GuestRow guest, CancellationToken ct)
    {
        if (guest.PrincipalId is { } existing) return existing;

        var principal = new PrincipalRow
        {
            PrincipalId = Uuid7.New(),
            PrincipalType = "Guest",
            DisplayName = guest.DisplayAlias ?? guest.PreferredName ?? "Guest",
        };
        // Two saves on purpose: the model carries no navigation between the two
        // rows, so EF cannot order the insert before the update that points at it.
        db.Add(principal);
        await db.SaveChangesAsync(ct);
        guest.PrincipalId = principal.PrincipalId;
        await db.SaveChangesAsync(ct);

        outbox.Enqueue(new OutboxEvent(EventTypes.GuestOwnershipChanged, "guest", guest.GuestId, guest.Version,
            new { principalId = principal.PrincipalId }, TenantWide: true));
        return principal.PrincipalId;
    }

    public sealed record Redeemed(Guid GuestId, Guid TenantId, Guid PrincipalId, string Purpose, string? ScopeEntityType, Guid? ScopeEntityId);

    /// <summary>Consumes the link. Null when unknown, used, revoked or expired — deliberately indistinguishable.</summary>
    public async Task<Redeemed?> RedeemAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 100) return null;
        var hash = SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(token.Trim()));
        var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        await using var cmd = new NpgsqlCommand(
            "SELECT guest_id, tenant_id, principal_id, purpose, scope_entity_type, scope_entity_id FROM guest.resolve_magic_link(@h)",
            conn, (NpgsqlTransaction)Microsoft.EntityFrameworkCore.Storage.DbContextTransactionExtensions.GetDbTransaction(db.Database.CurrentTransaction!));
        cmd.Parameters.AddWithValue("h", hash);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        if (!await r.ReadAsync(ct)) return null;
        return new Redeemed(r.GetGuid(0), r.GetGuid(1), r.GetGuid(2), r.GetString(3),
            r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetGuid(5));
    }

    private static string Base64Url(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
