using Spms.Modules.Scheduling.Domain;
using Spms.SharedKernel;

namespace Spms.Tests.Support;

/*
 * Port decorators for the Postgres world: readable names in, UUIDs to the
 * store, names back out — and each call in its own unit of work, the way a
 * request would run it. Inside a SchedulingService unit of work the inner call
 * becomes a savepoint, so the service's own transaction semantics are intact.
 */

internal static class Tx
{
    public static async Task<T> Run<T>(IUnitOfWork uow, Func<Task<T>> work)
    {
        await using var tx = await uow.BeginAsync();
        var r = await work();
        await tx.CommitAsync();
        return r;
    }
}

internal static class Map
{
    public static Appointment ToDb(Appointment a) => Appointment.Rehydrate(a.Snapshot() with
    {
        AppointmentId = TestIds.Of(a.AppointmentId), TenantId = TestIds.Of(a.TenantId), PropertyId = TestIds.Of(a.PropertyId),
        GuestId = TestIds.Of(a.GuestId), ServiceId = TestIds.Of(a.ServiceId),
        ProviderId = TestIds.OfNullable(a.ProviderId), RoomId = TestIds.OfNullable(a.RoomId), VisitId = TestIds.OfNullable(a.VisitId),
    });

    public static Appointment ToNames(Appointment a) => Appointment.Rehydrate(a.Snapshot() with
    {
        AppointmentId = TestIds.NameOf(a.AppointmentId), TenantId = TestIds.NameOf(a.TenantId), PropertyId = TestIds.NameOf(a.PropertyId),
        GuestId = TestIds.NameOf(a.GuestId), ServiceId = TestIds.NameOf(a.ServiceId),
        ProviderId = TestIds.NameOfNullable(a.ProviderId), RoomId = TestIds.NameOfNullable(a.RoomId), VisitId = TestIds.NameOfNullable(a.VisitId),
    });

    public static MoveProposal ToDb(MoveProposal p) => p with
    {
        AppointmentId = TestIds.Of(p.AppointmentId), ProviderId = TestIds.OfNullable(p.ProviderId), RoomId = TestIds.OfNullable(p.RoomId),
    };

    public static MoveProposal ToNames(MoveProposal p) => p with
    {
        AppointmentId = TestIds.NameOf(p.AppointmentId), ProviderId = TestIds.NameOfNullable(p.ProviderId), RoomId = TestIds.NameOfNullable(p.RoomId),
    };

    public static PreflightResult ToDb(PreflightResult r) => r with { Proposal = ToDb(r.Proposal), PropertyId = TestIds.Of(r.PropertyId) };
    public static PreflightResult ToNames(PreflightResult r) => r with { Proposal = ToNames(r.Proposal), PropertyId = TestIds.NameOf(r.PropertyId) };
}

public sealed class TranslatingRepository(IAppointmentRepository inner, IUnitOfWork uow) : IAppointmentRepository
{
    public Task<Appointment?> GetAsync(string tenantId, string propertyId, string appointmentId, CancellationToken ct = default) =>
        Tx.Run(uow, async () =>
        {
            var a = await inner.GetAsync(TestIds.Of(tenantId), TestIds.Of(propertyId), TestIds.Of(appointmentId), ct);
            return a is null ? null : Map.ToNames(a);
        });

    public Task<IReadOnlyList<Appointment>> ListOverlappingAsync(string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        Tx.Run(uow, async () => (IReadOnlyList<Appointment>)(await inner.ListOverlappingAsync(TestIds.Of(tenantId), TestIds.Of(propertyId), fromUtc, toUtc, ct)).Select(Map.ToNames).ToList());

    public Task<IReadOnlyList<Appointment>> ListPageAsync(string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, int offset, int limit, CancellationToken ct = default) =>
        Tx.Run(uow, async () => (IReadOnlyList<Appointment>)(await inner.ListPageAsync(TestIds.Of(tenantId), TestIds.Of(propertyId), fromUtc, toUtc, offset, limit, ct)).Select(Map.ToNames).ToList());

    public Task<int> CountOverlappingAsync(string tenantId, string propertyId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        Tx.Run(uow, () => inner.CountOverlappingAsync(TestIds.Of(tenantId), TestIds.Of(propertyId), fromUtc, toUtc, ct));

    public async Task<bool> TryAddAsync(Appointment appointment, CancellationToken ct = default)
    {
        try
        {
            return await Tx.Run(uow, () => inner.TryAddAsync(Map.ToDb(appointment), ct));
        }
        catch (RoomOverlapException e)
        {
            throw new RoomOverlapException(TestIds.NameOfNullable(e.RoomId), e);
        }
    }

    public async Task<bool> TryUpdateAsync(Appointment appointment, int expectedRowVersion, CancellationToken ct = default)
    {
        try
        {
            return await Tx.Run(uow, () => inner.TryUpdateAsync(Map.ToDb(appointment), expectedRowVersion, ct));
        }
        catch (RoomOverlapException e)
        {
            throw new RoomOverlapException(TestIds.NameOfNullable(e.RoomId), e);
        }
    }

    public Task<bool> ConfirmationNumberExistsAsync(string tenantId, string confirmationNumber, CancellationToken ct = default) =>
        Tx.Run(uow, () => inner.ConfirmationNumberExistsAsync(TestIds.Of(tenantId), confirmationNumber, ct));

    public Task<IReadOnlyList<GuestBusyInterval>> GuestBusyAsync(string tenantId, string guestId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        Tx.Run(uow, async () => (IReadOnlyList<GuestBusyInterval>)(await inner.GuestBusyAsync(TestIds.Of(tenantId), TestIds.Of(guestId), fromUtc, toUtc, ct))
            .Select(b => b with { PropertyId = TestIds.NameOf(b.PropertyId), AppointmentId = TestIds.NameOf(b.AppointmentId) }).ToList());
}

public sealed class TranslatingPreflights(IPreflightStore inner, IUnitOfWork uow) : IPreflightStore
{
    public Task SaveAsync(string tenantId, PreflightResult result, CancellationToken ct = default) =>
        Tx.Run(uow, async () => { await inner.SaveAsync(TestIds.Of(tenantId), Map.ToDb(result), ct); return true; });

    public Task<PreflightResult?> FindAsync(string tenantId, string token, CancellationToken ct = default) =>
        Tx.Run(uow, async () =>
        {
            var r = await inner.FindAsync(TestIds.Of(tenantId), token, ct);
            return r is null ? null : Map.ToNames(r);
        });

    public Task<bool> TryConsumeAsync(string tenantId, string token, DateTimeOffset nowUtc, DateTimeOffset? undoUntilUtc, string? reason, CancellationToken ct = default) =>
        Tx.Run(uow, () => inner.TryConsumeAsync(TestIds.Of(tenantId), token, nowUtc, undoUntilUtc, reason, ct));

    public Task<int> EvictExpiredAsync(DateTimeOffset nowUtc, CancellationToken ct = default) =>
        Tx.Run(uow, () => inner.EvictExpiredAsync(nowUtc, ct));
}

public sealed class TranslatingCatalog(IServiceCatalog inner, IUnitOfWork uow) : IServiceCatalog
{
    public Task<CatalogService?> FindAsync(string tenantId, string serviceId, CancellationToken ct = default) =>
        Tx.Run(uow, async () =>
        {
            var s = await inner.FindAsync(TestIds.Of(tenantId), TestIds.Of(serviceId), ct);
            return s is null ? null : s with { ServiceId = TestIds.NameOf(s.ServiceId) };
        });

    public Task<IReadOnlyList<CatalogService>> ListAsync(string tenantId, CancellationToken ct = default) =>
        Tx.Run(uow, async () => (IReadOnlyList<CatalogService>)(await inner.ListAsync(TestIds.Of(tenantId), ct))
            .Select(s => s with { ServiceId = TestIds.NameOf(s.ServiceId) }).ToList());
}

public sealed class TranslatingQualifications(IQualificationRegister inner, IUnitOfWork uow) : IQualificationRegister
{
    public Task<bool> IsQualifiedAsync(string tenantId, string propertyId, string providerId, string serviceId, DateTimeOffset asOfUtc, CancellationToken ct = default) =>
        Tx.Run(uow, () => inner.IsQualifiedAsync(TestIds.Of(tenantId), TestIds.Of(propertyId), TestIds.Of(providerId), TestIds.Of(serviceId), asOfUtc, ct));

    public Task<bool> IsKnownAsync(string tenantId, string propertyId, string providerId, CancellationToken ct = default) =>
        Tx.Run(uow, () => inner.IsKnownAsync(TestIds.Of(tenantId), TestIds.Of(propertyId), TestIds.Of(providerId), ct));
}

public sealed class TranslatingProperties(IPropertyDirectory inner, IUnitOfWork uow) : IPropertyDirectory
{
    public Task<PropertyProfile?> FindAsync(string tenantId, string propertyId, CancellationToken ct = default) =>
        Tx.Run(uow, async () =>
        {
            var p = await inner.FindAsync(TestIds.Of(tenantId), TestIds.Of(propertyId), ct);
            return p is null ? null : p with
            {
                PropertyId = TestIds.NameOf(p.PropertyId),
                BuffersByService = p.BuffersByService.ToDictionary(kv => TestIds.NameOf(kv.Key), kv => kv.Value),
            };
        });
}

public sealed class TranslatingGuests(IGuestDirectory inner, IUnitOfWork uow) : IGuestDirectory
{
    public Task<string?> AliasAsync(string tenantId, string guestId, CancellationToken ct = default) =>
        Tx.Run(uow, () => inner.AliasAsync(TestIds.Of(tenantId), TestIds.Of(guestId), ct));
}

public sealed class TranslatingAudit(IAuditSink inner) : IAuditSink
{
    public Task RecordAsync(AuditEntry entry, CancellationToken ct = default) =>
        inner.RecordAsync(entry with { EntityId = TestIds.Of(entry.EntityId) }, ct);
}

public sealed class TranslatingCalendar(IResourceCalendar inner, IUnitOfWork uow) : IResourceCalendar
{
    public Task<RoomState> RoomAsync(string tenantId, string propertyId, string roomId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        Tx.Run(uow, () => inner.RoomAsync(TestIds.Of(tenantId), TestIds.Of(propertyId), TestIds.Of(roomId), fromUtc, toUtc, ct));

    public Task<RosterState> ProviderAsync(string tenantId, string propertyId, string providerId, DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default) =>
        Tx.Run(uow, () => inner.ProviderAsync(TestIds.Of(tenantId), TestIds.Of(propertyId), TestIds.Of(providerId), fromUtc, toUtc, ct));
}
