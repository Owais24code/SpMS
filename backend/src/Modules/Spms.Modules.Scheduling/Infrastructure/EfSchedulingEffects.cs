using Microsoft.EntityFrameworkCore;
using Spms.Modules.Scheduling.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Scheduling.Infrastructure;

/// <summary>
/// The visit and the room follow the appointment, in the appointment's own
/// transaction: the first guest of a party checking in is the party arriving,
/// the first treatment starting is the visit in progress, and a finished
/// treatment leaves its room needing a turnover before the next guest.
/// </summary>
public sealed class EfSchedulingEffects(SpmsDbContext db, IOutbox outbox, IEnumerable<ISchedulingObserver> observers) : ISchedulingEffects
{
    public async Task TransitionedAsync(Appointment after, AppointmentStatus from, BufferPolicy buffers, DateTimeOffset nowUtc, CancellationToken ct = default)
    {
        var now = nowUtc.ToUniversalTime();
        var principal = db.Scope.PrincipalId;

        if (Guid.TryParse(after.VisitId, out var visitId))
        {
            var (fromStatus, toStatus) = after.Status switch
            {
                AppointmentStatus.CheckedIn => (VisitStatuses.Planned, VisitStatuses.Arrived),
                AppointmentStatus.InService => (VisitStatuses.Arrived, VisitStatuses.InProgress),
                _ => ((string?)null, (string?)null),
            };
            if (fromStatus is not null)
            {
                var moved = await db.Set<VisitRow>()
                    .Where(v => v.VisitId == visitId && v.Status == fromStatus)
                    .ExecuteUpdateAsync(u => u
                        .SetProperty(v => v.Status, toStatus!)
                        .SetProperty(v => v.ActualArrivalAt, v => toStatus == VisitStatuses.Arrived ? now : v.ActualArrivalAt)
                        .SetProperty(v => v.Version, v => v.Version + 1)
                        .SetProperty(v => v.UpdatedBy, principal), ct);
                if (moved == 1)
                    outbox.Enqueue(new OutboxEvent(EventTypes.VisitChanged, "visit", visitId, null,
                        new { visitId, from = fromStatus, to = toStatus, cause = "appointment", appointmentId = after.AppointmentId }));
            }
        }

        if (after.Status == AppointmentStatus.Completed && Guid.TryParse(after.RoomId, out var roomId)
            && Guid.TryParse(after.AppointmentId, out var appointmentId) && Guid.TryParse(after.PropertyId, out var propertyId))
        {
            var task = new TurnaroundTaskRow
            {
                PropertyId = propertyId,
                AppointmentId = appointmentId,
                ResourceId = roomId,
                TaskType = "Turnover",
                DueAt = now.AddMinutes(Math.Max(0, buffers.RoomTurnoverMinutes)),
                Status = TurnaroundTaskStatuses.Pending,
            };
            db.Add(task);
            await db.SaveChangesAsync(ct);
            db.Entry(task).State = EntityState.Detached;
            outbox.Enqueue(new OutboxEvent(EventTypes.TurnaroundChanged, "turnaround_task", task.TurnaroundTaskId, 1,
                new { turnaroundTaskId = task.TurnaroundTaskId, roomId, appointmentId, status = task.Status, dueUtc = task.DueAt }));
        }

        foreach (var o in observers) await o.TransitionedAsync(after, from, nowUtc, ct);
    }
}
