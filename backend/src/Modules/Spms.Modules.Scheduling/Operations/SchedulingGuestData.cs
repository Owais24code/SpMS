using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Scheduling.Operations;

/// <summary>
/// Scheduling's part of a privacy request: the guest's bookings, visits and
/// waitlist entries. Bookings are financial and operational records and are
/// kept on erasure (the guest record they point at is emptied); open waitlist
/// entries are cancelled, since nobody should be offered a slot any more.
/// </summary>
public sealed class SchedulingGuestData(SpmsDbContext db) : IGuestDataContributor
{
    public string Section => "scheduling";

    public async Task<object?> ExportAsync(Guid guestId, CancellationToken ct)
    {
        var appointments = await (from a in db.Set<AppointmentRow>().AsNoTracking()
                                  where a.GuestId == guestId
                                  join s in db.Set<ServiceRow>() on a.ServiceId equals s.ServiceId
                                  orderby a.StartAt
                                  select new { a.AppointmentId, a.PropertyId, service = s.Name, a.StartAt, a.EndAt, a.Status, a.Source, a.PriceMinor, a.CurrencyCode })
            .ToListAsync(ct);
        var visits = await db.Set<VisitRow>().AsNoTracking().Where(v => v.PrimaryGuestId == guestId)
            .Select(v => new { v.VisitId, v.PropertyId, v.VisitDate, v.VisitType, v.Status }).ToListAsync(ct);
        var waitlist = await db.Set<WaitlistEntryRow>().AsNoTracking().Where(w => w.GuestId == guestId)
            .Select(w => new { w.WaitlistId, w.PropertyId, w.Status, w.CreatedAt }).ToListAsync(ct);
        return new { appointments, visits, waitlist };
    }

    public Task<int> EraseAsync(Guid guestId, CancellationToken ct) =>
        db.Set<WaitlistEntryRow>()
            .Where(w => w.GuestId == guestId && (w.Status == WaitlistEntryStatuses.Waiting || w.Status == WaitlistEntryStatuses.Offered))
            .ExecuteUpdateAsync(u => u.SetProperty(w => w.Status, WaitlistEntryStatuses.Cancelled)
                .SetProperty(w => w.OfferExpiresAt, (DateTimeOffset?)null)
                .SetProperty(w => w.Version, w => w.Version + 1).SetProperty(w => w.UpdatedBy, db.Scope.PrincipalId), ct);
}
