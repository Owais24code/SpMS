using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Spms.Modules.Core.Data;
using Spms.Modules.Scheduling.Domain;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Tests.Support;

/// <summary>
/// The Postgres-backed equivalent of <see cref="SchedulingWorld"/>: the same
/// ports and names, the real adapters underneath (through
/// <see cref="TestIds"/> translation), one application scope per world.
/// </summary>
public sealed class PostgresWorld : IAsyncDisposable
{
    public const string Tenant = SchedulingWorld.Tenant;
    public const string Property = SchedulingWorld.Property;

    private readonly IServiceScope _scope;

    public IAppointmentRepository Repo { get; }
    public IPreflightStore Preflights { get; }
    public IIdempotencyStore Idempotency { get; }
    public IUnitOfWork UnitOfWork { get; }
    public SchedulingService Scheduling { get; }
    public TestClock Clock { get; }
    public DateTimeOffset Day { get; }
    public SpmsDbContext Db { get; }
    public IServiceProvider Services => _scope.ServiceProvider;

    public PostgresWorld(PostgresFixture fixture, DateTimeOffset? day = null, string tenant = Tenant, string property = Property)
    {
        Day = day ?? new DateTimeOffset(2026, 6, 15, 0, 0, 0, TimeSpan.Zero);
        Clock = new TestClock(Day.AddHours(8));
        _scope = fixture.NewScope(tenant, [property], property, Clock);

        var sp = _scope.ServiceProvider;
        Db = sp.GetRequiredService<SpmsDbContext>();
        UnitOfWork = sp.GetRequiredService<IUnitOfWork>();
        Repo = new TranslatingRepository(sp.GetRequiredService<IAppointmentRepository>(), UnitOfWork);
        Preflights = new TranslatingPreflights(sp.GetRequiredService<IPreflightStore>(), UnitOfWork);
        Idempotency = sp.GetRequiredService<IIdempotencyStore>();

        Scheduling = new SchedulingService(
            Repo, Preflights, new TranslatingAudit(sp.GetRequiredService<IAuditSink>()), UnitOfWork,
            new TranslatingCatalog(sp.GetRequiredService<IServiceCatalog>(), UnitOfWork),
            new TranslatingQualifications(sp.GetRequiredService<IQualificationRegister>(), UnitOfWork),
            new TranslatingProperties(sp.GetRequiredService<IPropertyDirectory>(), UnitOfWork),
            new TranslatingGuests(sp.GetRequiredService<IGuestDirectory>(), UnitOfWork),
            new TranslatingCalendar(sp.GetRequiredService<IResourceCalendar>(), UnitOfWork),
            sp.GetRequiredService<IOutbox>(),
            Clock);
    }

    public Task<Appointment?> Get(string id) => Repo.GetAsync(Tenant, Property, id);

    public Task<SchedulingService.CommitResult> Commit(string routeId, string token, string? reason = null) =>
        Scheduling.CommitMoveAsync(Tenant, Property, routeId, token, reason, "corr-test");

    public Task<PreflightResult> Preflight(Appointment target, DateTimeOffset startUtc, string? provider, string? room) =>
        Scheduling.PreflightAsync(Tenant, Property, target,
            new MoveProposal(target.AppointmentId, startUtc, provider, room, target.RowVersion));

    public async Task AddAsync(Appointment a)
    {
        if (!await Repo.TryAddAsync(a)) throw new InvalidOperationException($"Fixture collided on {a.AppointmentId}.");
    }

    public static Appointment Appointment(
        string id, AppointmentStatus status, DateTimeOffset startUtc, int minutes,
        string? provider = null, string? room = null,
        string serviceId = "svc-swedish", string serviceName = "Swedish 60",
        string guestId = "guest-0000", string alias = "Guest", string? confirmation = null) =>
        SchedulingWorld.Appointment(id, status, startUtc, minutes, provider, room, serviceId, serviceName, guestId, alias, confirmation);

    public sealed record AuditRow(string Action, string SubjectId, string? ToStatus, string? ReasonText, Guid? Actor);

    /// <summary>The property's audit rows, newest first, with entity ids translated back to names.</summary>
    public async Task<IReadOnlyList<AuditRow>> AuditRecentAsync(int take)
    {
        await using var tx = await UnitOfWork.BeginAsync();
        var rows = await Db.Set<AuditEventRow>().AsNoTracking()
            .OrderByDescending(a => a.OccurredAt).ThenByDescending(a => a.AuditId).Take(take).ToListAsync();
        await tx.CommitAsync();
        return rows.Select(r => new AuditRow(r.Action, TestIds.NameOf(r.EntityId.ToString()), r.ToStatus, r.ReasonText, r.ActorPrincipalId)).ToList();
    }

    public ValueTask DisposeAsync()
    {
        _scope.Dispose();
        return ValueTask.CompletedTask;
    }
}
