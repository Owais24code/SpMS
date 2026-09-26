using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Core.Data;
using Spms.Modules.Guest.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Scheduling.Operations;

public static class VisitTypes
{
    public static readonly IReadOnlySet<string> All =
        new HashSet<string>(StringComparer.Ordinal) { "DayGuest", "HotelGuest", "Member", "Group", "WalkIn" };
}

public sealed record VisitAppointment(
    Guid AppointmentId, string ServiceName, Guid? ProviderId, Guid? RoomId, DateTimeOffset StartUtc, DateTimeOffset EndUtc, string Status, int Version);

public sealed record VisitView(VisitRow Visit, string GuestAlias, IReadOnlyList<VisitAppointment> Appointments);

/// <summary>
/// The visit: a party's day (GOLDEN_GUEST_JOURNEY), planned, then arrived,
/// then closed. Arrival and in-progress normally follow the appointments
/// (<see cref="Infrastructure.EfSchedulingEffects"/>); the desk moves it by
/// hand to close it, or to cancel or no-show the whole party.
/// </summary>
public sealed class VisitService(SpmsDbContext db, SchedulingService scheduling, IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    private static readonly Dictionary<string, string[]> Allowed = new(StringComparer.Ordinal)
    {
        [VisitStatuses.Planned] = [VisitStatuses.Arrived, VisitStatuses.Cancelled, VisitStatuses.NoShow],
        [VisitStatuses.Arrived] = [VisitStatuses.InProgress, VisitStatuses.Closed, VisitStatuses.Cancelled],
        [VisitStatuses.InProgress] = [VisitStatuses.Closed],
        [VisitStatuses.Closed] = [],
        [VisitStatuses.Cancelled] = [],
        [VisitStatuses.NoShow] = [],
    };

    public static IReadOnlyList<string> NextFrom(string status) => Allowed.TryGetValue(status, out var n) ? n : [];

    private static readonly string[] InTreatment = [AppointmentStatuses.CheckedIn, AppointmentStatuses.Ready, AppointmentStatuses.InService];

    public enum Outcome { Ok, NotFound, UnknownGuest, StaleVersion, Illegal, Blocked }

    public sealed record Result(Outcome Outcome, VisitView? View = null, string? Detail = null);

    public async Task<Result> CreateAsync(Guid guestId, DateOnly date, string visitType, DateTimeOffset? scheduledArrivalUtc,
        string? notes, string? pmsStayReference, CancellationToken ct)
    {
        var propertyId = db.Scope.RequireProperty();
        await using var tx = await uow.BeginAsync(ct);

        if (!await db.Set<GuestRow>().AnyAsync(g => g.GuestId == guestId && (g.Status == GuestStatuses.Active || g.Status == GuestStatuses.Restricted), ct))
            return new Result(Outcome.UnknownGuest);
        var mode = await db.Set<PropertyRow>().Where(p => p.PropertyId == propertyId).Select(p => p.OperatingMode).SingleAsync(ct);

        var row = new VisitRow
        {
            PropertyId = propertyId,
            VisitType = visitType,
            PrimaryGuestId = guestId,
            VisitDate = date,
            // Frozen: the mode this visit is operated under, whatever the
            // property switches to later in the day.
            OperatingMode = mode,
            ScheduledArrivalAt = scheduledArrivalUtc?.ToUniversalTime(),
            Notes = notes,
            PmsStayReference = pmsStayReference,
            Status = VisitStatuses.Planned,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);

        await audit.RecordAsync(new AuditEntry("visit.create", "visit", row.VisitId.ToString(), row.Version,
            Purpose: "scheduling", ToStatus: row.Status, AfterData: new { visitType, date, guestId }), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.VisitChanged, "visit", row.VisitId, row.Version,
            new { visitId = row.VisitId, to = row.Status, visitType, visitDate = date }));
        db.Entry(row).State = EntityState.Detached;
        var view = await ViewAsync(row.VisitId, ct);
        await tx.CommitAsync(ct);
        return new Result(Outcome.Ok, view);
    }

    public async Task<VisitView?> ViewAsync(Guid visitId, CancellationToken ct)
    {
        var v = await db.Set<VisitRow>().AsNoTracking().SingleOrDefaultAsync(x => x.VisitId == visitId, ct);
        if (v is null) return null;
        var alias = await db.Set<GuestRow>().Where(g => g.GuestId == v.PrimaryGuestId)
            .Select(g => g.DisplayAlias ?? g.PreferredName ?? "Guest").SingleOrDefaultAsync(ct) ?? "Guest";
        var appointments = await (
            from a in db.Set<AppointmentRow>().AsNoTracking()
            where a.VisitId == visitId
            join s in db.Set<ServiceRow>() on a.ServiceId equals s.ServiceId
            orderby a.StartAt
            select new VisitAppointment(a.AppointmentId, s.Name, a.ProviderId, a.RoomId, a.StartAt, a.EndAt, a.Status, a.Version))
            .ToListAsync(ct);
        return new VisitView(v, alias, appointments);
    }

    public async Task<(IReadOnlyList<VisitView> Items, int Total)> ListAsync(DateOnly date, string? status, int offset, int limit, CancellationToken ct)
    {
        var q = db.Set<VisitRow>().AsNoTracking().Where(v => v.VisitDate == date);
        if (status is not null) q = q.Where(v => v.Status == status);
        var total = await q.CountAsync(ct);
        var ids = await q.OrderBy(v => v.ScheduledArrivalAt ?? v.CreatedAt).ThenBy(v => v.VisitId)
            .Skip(offset).Take(limit).Select(v => v.VisitId).ToListAsync(ct);
        var views = new List<VisitView>();
        foreach (var id in ids) if (await ViewAsync(id, ct) is { } view) views.Add(view);
        return (views, total);
    }

    /// <summary>
    /// A hand-made transition. Cancelling or no-showing the party does the
    /// same to its bookings that have not started, through the scheduling
    /// service so each one is audited and published like any cancellation.
    /// </summary>
    public async Task<Result> TransitionAsync(Guid visitId, string to, int expectedVersion, string? reason, CancellationToken ct)
    {
        var tenant = db.Scope.RequireTenant().ToString();
        var property = db.Scope.RequireProperty().ToString();
        await using var tx = await uow.BeginAsync(ct);

        var v = await db.Set<VisitRow>().SingleOrDefaultAsync(x => x.VisitId == visitId, ct);
        if (v is null) return new Result(Outcome.NotFound);
        if (v.Version != expectedVersion) return new Result(Outcome.StaleVersion, await ViewAsync(visitId, ct));
        if (!NextFrom(v.Status).Contains(to))
            return new Result(Outcome.Illegal, await ViewAsync(visitId, ct), $"{v.Status} cannot move to {to}.");

        var appointments = await db.Set<AppointmentRow>().AsNoTracking().Where(a => a.VisitId == visitId).ToListAsync(ct);
        var now = clock.UtcNow;

        if (to == VisitStatuses.Closed && appointments.Any(a => InTreatment.Contains(a.Status)))
            return new Result(Outcome.Blocked, await ViewAsync(visitId, ct), "A guest of this visit is still checked in or in treatment.");

        if (to is VisitStatuses.Cancelled or VisitStatuses.NoShow)
        {
            if (appointments.Any(a => InTreatment.Contains(a.Status) || a.Status == AppointmentStatuses.Completed))
                return new Result(Outcome.Blocked, await ViewAsync(visitId, ct), "Part of this visit has already been delivered.");

            var target = to == VisitStatuses.NoShow ? AppointmentStatus.NoShow : AppointmentStatus.Cancelled;
            foreach (var a in appointments.Where(a => a.Status is AppointmentStatuses.Held or AppointmentStatuses.Confirmed))
            {
                // A Held booking cannot be a no-show (it was never confirmed); it is released.
                var next = a.Status == AppointmentStatuses.Held ? AppointmentStatus.Cancelled : target;
                var r = await scheduling.TransitionAsync(tenant, property, a.AppointmentId.ToString(), next, a.Version,
                    reason ?? $"Visit {to.ToLowerInvariant()}", db.Scope.CorrelationId ?? "visit", ct,
                    next == AppointmentStatus.Cancelled ? "VisitCancelled" : null);
                if (r.Outcome != SchedulingService.TransitionOutcome.Applied)
                    return new Result(Outcome.StaleVersion, await ViewAsync(visitId, ct), "A booking of this visit changed; re-read and try again.");
            }
        }

        var from = v.Status;
        v.Status = to;
        if (to == VisitStatuses.Arrived) v.ActualArrivalAt ??= now;
        if (to == VisitStatuses.Closed) { v.ClosedAt = now; v.ActualDepartureAt ??= now; }
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return new Result(Outcome.StaleVersion, null);
        }

        await audit.RecordAsync(new AuditEntry("visit.transition", "visit", visitId.ToString(), v.Version,
            Purpose: "scheduling", FromStatus: from, ToStatus: to, ReasonText: reason), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.VisitChanged, "visit", visitId, v.Version, new { visitId, from, to }));
        db.Entry(v).State = EntityState.Detached;
        var view = await ViewAsync(visitId, ct);
        await tx.CommitAsync(ct);
        return new Result(Outcome.Ok, view);
    }
}
