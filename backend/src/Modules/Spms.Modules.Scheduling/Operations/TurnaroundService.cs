using Microsoft.EntityFrameworkCore;
using Spms.Modules.Resources.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Workforce.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Scheduling.Operations;

public sealed record TurnaroundView(TurnaroundTaskRow Row, string RoomName);

/// <summary>
/// Room readiness: a completed treatment leaves a Turnover task (created by
/// <see cref="Infrastructure.EfSchedulingEffects"/>), housekeeping starts and
/// completes it with a result, and a room with an open task is not ready for
/// the next arrival. Deep cleans, sanitation and restocks are added by hand.
/// </summary>
public sealed class TurnaroundService(SpmsDbContext db, IAuditSink audit, IOutbox outbox, IUnitOfWork uow, IClock clock)
{
    public static readonly IReadOnlySet<string> TaskTypes = new HashSet<string>(StringComparer.Ordinal) { "Turnover", "DeepClean", "Sanitation", "Restock" };
    public static readonly IReadOnlySet<string> Results = new HashSet<string>(StringComparer.Ordinal) { "Pass", "Fail", "NeedsAttention" };

    public enum Outcome { Ok, NotFound, UnknownRoom, StaleVersion, Illegal }

    public sealed record Result(Outcome Outcome, TurnaroundView? View = null, string? Detail = null);

    public async Task<(IReadOnlyList<TurnaroundView> Items, int Total)> ListAsync(bool openOnly, int offset, int limit, CancellationToken ct)
    {
        var q = from t in db.Set<TurnaroundTaskRow>().AsNoTracking()
                join r in db.Set<ResourceRow>() on t.ResourceId equals r.ResourceId
                select new { t, r.Name };
        if (openOnly) q = q.Where(x => x.t.Status == TurnaroundTaskStatuses.Pending || x.t.Status == TurnaroundTaskStatuses.InProgress);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderBy(x => x.t.DueAt).ThenBy(x => x.t.TurnaroundTaskId).Skip(offset).Take(limit).ToListAsync(ct);
        return (rows.Select(x => new TurnaroundView(x.t, x.Name)).ToList(), total);
    }

    /// <summary>Rooms with an open task: not ready for the next guest.</summary>
    public async Task<IReadOnlySet<Guid>> RoomsNotReadyAsync(CancellationToken ct) =>
        (await db.Set<TurnaroundTaskRow>().AsNoTracking()
            .Where(t => t.Status == TurnaroundTaskStatuses.Pending || t.Status == TurnaroundTaskStatuses.InProgress)
            .Select(t => t.ResourceId).Distinct().ToListAsync(ct)).ToHashSet();

    public async Task<TurnaroundView?> ViewAsync(Guid id, CancellationToken ct)
    {
        var hit = await (from t in db.Set<TurnaroundTaskRow>().AsNoTracking()
                         where t.TurnaroundTaskId == id
                         join r in db.Set<ResourceRow>() on t.ResourceId equals r.ResourceId
                         select new { t, r.Name }).SingleOrDefaultAsync(ct);
        return hit is null ? null : new TurnaroundView(hit.t, hit.Name);
    }

    public async Task<Result> AddAsync(Guid roomId, string taskType, DateTimeOffset dueUtc, string? checklistCode, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        if (!await db.Set<ResourceRow>().AnyAsync(r => r.ResourceId == roomId, ct)) return new Result(Outcome.UnknownRoom);
        var row = new TurnaroundTaskRow
        {
            PropertyId = db.Scope.RequireProperty(), ResourceId = roomId, TaskType = taskType,
            DueAt = dueUtc.ToUniversalTime(), ChecklistCode = checklistCode, Status = TurnaroundTaskStatuses.Pending,
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        await RecordAsync(row, "turnaround.add", null, null, ct);
        db.Entry(row).State = EntityState.Detached;
        var view = await ViewAsync(row.TurnaroundTaskId, ct);
        await tx.CommitAsync(ct);
        return new Result(Outcome.Ok, view);
    }

    public Task<Result> StartAsync(Guid id, int expectedVersion, CancellationToken ct) =>
        ChangeAsync(id, expectedVersion, "turnaround.start", [TurnaroundTaskStatuses.Pending], null, ct, async row =>
        {
            row.Status = TurnaroundTaskStatuses.InProgress;
            row.AssignedStaffId ??= await CallerStaffAsync(ct);
        });

    public Task<Result> CompleteAsync(Guid id, int expectedVersion, string result, string? checklistCode, CancellationToken ct) =>
        ChangeAsync(id, expectedVersion, "turnaround.complete", [TurnaroundTaskStatuses.Pending, TurnaroundTaskStatuses.InProgress], null, ct, async row =>
        {
            row.Status = TurnaroundTaskStatuses.Completed;
            row.Result = result;
            row.ChecklistCode = checklistCode ?? row.ChecklistCode;
            row.CompletedAt = clock.UtcNow;
            row.AssignedStaffId ??= await CallerStaffAsync(ct);
        });

    public Task<Result> SkipAsync(Guid id, int expectedVersion, string reason, CancellationToken ct) =>
        ChangeAsync(id, expectedVersion, "turnaround.skip", [TurnaroundTaskStatuses.Pending, TurnaroundTaskStatuses.InProgress], reason, ct, row =>
        {
            row.Status = TurnaroundTaskStatuses.Skipped;
            return Task.CompletedTask;
        });

    private async Task<Guid?> CallerStaffAsync(CancellationToken ct) =>
        db.Scope.PrincipalId is { } p
            ? await db.Set<StaffRow>().Where(s => s.PrincipalId == p).Select(s => (Guid?)s.StaffId).FirstOrDefaultAsync(ct)
            : null;

    private async Task<Result> ChangeAsync(Guid id, int expectedVersion, string action, string[] from, string? reason, CancellationToken ct,
        Func<TurnaroundTaskRow, Task> apply)
    {
        await using var tx = await uow.BeginAsync(ct);
        var row = await db.Set<TurnaroundTaskRow>().SingleOrDefaultAsync(t => t.TurnaroundTaskId == id, ct);
        if (row is null) return new Result(Outcome.NotFound);
        if (row.Version != expectedVersion || !from.Contains(row.Status))
        {
            var stale = row.Version != expectedVersion;
            db.Entry(row).State = EntityState.Detached;
            return new Result(stale ? Outcome.StaleVersion : Outcome.Illegal, await ViewAsync(id, ct),
                stale ? null : $"A {row.Status} task cannot do that.");
        }
        var previous = row.Status;
        await apply(row);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            db.Entry(row).State = EntityState.Detached;
            return new Result(Outcome.StaleVersion);
        }
        await RecordAsync(row, action, previous, reason, ct);
        db.Entry(row).State = EntityState.Detached;
        var view = await ViewAsync(id, ct);
        await tx.CommitAsync(ct);
        return new Result(Outcome.Ok, view);
    }

    private async Task RecordAsync(TurnaroundTaskRow row, string action, string? from, string? reason, CancellationToken ct)
    {
        await audit.RecordAsync(new AuditEntry(action, "turnaround_task", row.TurnaroundTaskId.ToString(), row.Version,
            Purpose: "room_readiness", FromStatus: from, ToStatus: row.Status, ReasonText: reason,
            AfterData: new { roomId = row.ResourceId, row.TaskType, row.Result }), ct);
        outbox.Enqueue(new OutboxEvent(EventTypes.TurnaroundChanged, "turnaround_task", row.TurnaroundTaskId, row.Version,
            new { turnaroundTaskId = row.TurnaroundTaskId, roomId = row.ResourceId, from, to = row.Status, result = row.Result }));
    }
}
