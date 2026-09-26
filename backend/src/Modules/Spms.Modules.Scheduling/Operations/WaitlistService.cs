using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Guest.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Scheduling.Operations;

/// <summary>What the guest would accept, stored as scheduling.waitlist_entry.criteria.</summary>
public sealed record WaitlistCriteria(DateTimeOffset EarliestUtc, DateTimeOffset LatestUtc, Guid? ProviderId = null, string? Notes = null);

public sealed record WaitlistView(WaitlistEntryRow Row, string GuestAlias, WaitlistCriteria Criteria);

/// <summary>
/// The waitlist (§Reschedule, cancel and waitlist): a guest waits for a window;
/// when a slot frees the desk sees who fits, offers it for a limited time, and
/// the entry is Accepted once the guest books. An offer that lapses puts the
/// guest back in the queue; an entry past its own expiry leaves it.
/// </summary>
public sealed class WaitlistService(SpmsDbContext db, IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public enum Outcome { Ok, NotFound, UnknownGuest, StaleVersion, Illegal, WrongGuest }

    public sealed record Result(Outcome Outcome, WaitlistView? View = null, string? Detail = null);

    public static string Serialize(WaitlistCriteria c) => JsonSerializer.Serialize(c, Json);

    public static WaitlistCriteria Parse(string json) =>
        JsonSerializer.Deserialize<WaitlistCriteria>(json, Json) ?? new WaitlistCriteria(DateTimeOffset.MinValue, DateTimeOffset.MinValue);

    public async Task<Result> AddAsync(Guid guestId, Guid? serviceId, WaitlistCriteria criteria, DateTimeOffset? expiresUtc, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        if (!await db.Set<GuestRow>().AnyAsync(g => g.GuestId == guestId
                && (g.Status == GuestStatuses.Active || g.Status == GuestStatuses.Restricted), ct))
            return new Result(Outcome.UnknownGuest);

        var row = new WaitlistEntryRow
        {
            PropertyId = db.Scope.RequireProperty(),
            GuestId = guestId,
            ServiceId = serviceId,
            Criteria = Serialize(criteria with { EarliestUtc = criteria.EarliestUtc.ToUniversalTime(), LatestUtc = criteria.LatestUtc.ToUniversalTime() }),
            ExpiresAt = expiresUtc?.ToUniversalTime(),
            Status = WaitlistEntryStatuses.Waiting,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await RecordAsync(row, "waitlist.add", null, ct);
        db.Entry(row).State = EntityState.Detached;
        var view = await ViewAsync(row.WaitlistId, ct);
        await tx.CommitAsync(ct);
        return new Result(Outcome.Ok, view);
    }

    public async Task<WaitlistView?> ViewAsync(Guid id, CancellationToken ct)
    {
        var hit = await (
            from w in db.Set<WaitlistEntryRow>().AsNoTracking()
            where w.WaitlistId == id
            join g in db.Set<GuestRow>() on w.GuestId equals g.GuestId
            select new { w, alias = g.DisplayAlias ?? g.PreferredName ?? "Guest" }).SingleOrDefaultAsync(ct);
        return hit is null ? null : new WaitlistView(hit.w, hit.alias, Parse(hit.w.Criteria));
    }

    public async Task<(IReadOnlyList<WaitlistView> Items, int Total)> ListAsync(string? status, int offset, int limit, CancellationToken ct)
    {
        var q = from w in db.Set<WaitlistEntryRow>().AsNoTracking()
                join g in db.Set<GuestRow>() on w.GuestId equals g.GuestId
                select new { w, alias = g.DisplayAlias ?? g.PreferredName ?? "Guest" };
        if (status is not null) q = q.Where(x => x.w.Status == status);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.w.CreatedAt).ThenBy(x => x.w.WaitlistId).Skip(offset).Take(limit).ToListAsync(ct);
        return (rows.Select(x => new WaitlistView(x.w, x.alias, Parse(x.w.Criteria))).ToList(), total);
    }

    /// <summary>
    /// Waiting guests a freed slot would suit, longest-waiting first: the
    /// window covers the slot, and the service (when they named one) matches.
    /// </summary>
    public async Task<IReadOnlyList<WaitlistView>> CandidatesAsync(DateTimeOffset startUtc, DateTimeOffset endUtc, Guid? serviceId, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var (waiting, _) = await ListAsync(WaitlistEntryStatuses.Waiting, 0, 500, ct);
        return waiting
            .Where(w => w.Row.ExpiresAt is null || w.Row.ExpiresAt > now)
            .Where(w => w.Row.ServiceId is null || serviceId is null || w.Row.ServiceId == serviceId)
            .Where(w => w.Criteria.EarliestUtc <= startUtc && endUtc <= w.Criteria.LatestUtc)
            .ToList();
    }

    public Task<Result> OfferAsync(Guid id, int expectedVersion, int minutes, CancellationToken ct) =>
        ChangeAsync(id, expectedVersion, "waitlist.offer", [WaitlistEntryStatuses.Waiting], ct, row =>
        {
            row.Status = WaitlistEntryStatuses.Offered;
            row.OfferExpiresAt = clock.UtcNow.AddMinutes(Math.Clamp(minutes, 5, 24 * 60));
            return Task.FromResult<Result?>(null);
        });

    public Task<Result> AcceptAsync(Guid id, int expectedVersion, Guid appointmentId, CancellationToken ct) =>
        ChangeAsync(id, expectedVersion, "waitlist.accept", [WaitlistEntryStatuses.Waiting, WaitlistEntryStatuses.Offered], ct, async row =>
        {
            var guest = await db.Set<AppointmentRow>().Where(a => a.AppointmentId == appointmentId)
                .Select(a => (Guid?)a.GuestId).SingleOrDefaultAsync(ct);
            if (guest is null) return new Result(Outcome.NotFound, null, "No such appointment at this property.");
            if (guest != row.GuestId) return new Result(Outcome.WrongGuest, null, "That appointment is for a different guest.");
            row.Status = WaitlistEntryStatuses.Accepted;
            row.AcceptedAppointmentId = appointmentId;
            row.OfferExpiresAt = null;
            return null;
        });

    public Task<Result> CancelAsync(Guid id, int expectedVersion, CancellationToken ct) =>
        ChangeAsync(id, expectedVersion, "waitlist.cancel", [WaitlistEntryStatuses.Waiting, WaitlistEntryStatuses.Offered], ct, row =>
        {
            row.Status = WaitlistEntryStatuses.Cancelled;
            row.OfferExpiresAt = null;
            return Task.FromResult<Result?>(null);
        });

    /// <summary>The expiry job: lapsed offers go back to Waiting; entries past their own expiry are Expired.</summary>
    public async Task<int> ExpireAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var principal = db.Scope.PrincipalId;
        var lapsed = await db.Set<WaitlistEntryRow>()
            .Where(w => w.Status == WaitlistEntryStatuses.Offered && w.OfferExpiresAt <= now)
            .ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Status, WaitlistEntryStatuses.Waiting)
                .SetProperty(w => w.OfferExpiresAt, (DateTimeOffset?)null)
                .SetProperty(w => w.Version, w => w.Version + 1)
                .SetProperty(w => w.UpdatedBy, principal), ct);
        var expired = await db.Set<WaitlistEntryRow>()
            .Where(w => (w.Status == WaitlistEntryStatuses.Waiting || w.Status == WaitlistEntryStatuses.Offered) && w.ExpiresAt <= now)
            .ExecuteUpdateAsync(u => u
                .SetProperty(w => w.Status, WaitlistEntryStatuses.Expired)
                .SetProperty(w => w.OfferExpiresAt, (DateTimeOffset?)null)
                .SetProperty(w => w.Version, w => w.Version + 1)
                .SetProperty(w => w.UpdatedBy, principal), ct);
        return lapsed + expired;
    }

    private async Task<Result> ChangeAsync(Guid id, int expectedVersion, string action, string[] from, CancellationToken ct,
        Func<WaitlistEntryRow, Task<Result?>> apply)
    {
        await using var tx = await uow.BeginAsync(ct);
        var row = await db.Set<WaitlistEntryRow>().SingleOrDefaultAsync(w => w.WaitlistId == id, ct);
        if (row is null) return new Result(Outcome.NotFound);
        if (row.Version != expectedVersion) { db.Entry(row).State = EntityState.Detached; return new Result(Outcome.StaleVersion, await ViewAsync(id, ct)); }
        if (!from.Contains(row.Status))
        {
            db.Entry(row).State = EntityState.Detached;
            return new Result(Outcome.Illegal, await ViewAsync(id, ct), $"A {row.Status} entry cannot do that.");
        }
        var previous = row.Status;
        if (await apply(row) is { } refused) { db.Entry(row).State = EntityState.Detached; return refused; }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(row).State = EntityState.Detached;
            return new Result(Outcome.StaleVersion);
        }
        await RecordAsync(row, action, previous, ct);
        db.Entry(row).State = EntityState.Detached;
        var view = await ViewAsync(id, ct);
        await tx.CommitAsync(ct);
        return new Result(Outcome.Ok, view);
    }

    private async Task RecordAsync(WaitlistEntryRow row, string action, string? from, CancellationToken ct)
    {
        await audit.RecordAsync(new AuditEntry(action, "waitlist_entry", row.WaitlistId.ToString(), row.Version,
            Purpose: "scheduling", FromStatus: from, ToStatus: row.Status), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.WaitlistChanged, "waitlist_entry", row.WaitlistId, row.Version,
            new { waitlistId = row.WaitlistId, guestId = row.GuestId, from, to = row.Status, offerExpiresUtc = row.OfferExpiresAt }));
    }
}
