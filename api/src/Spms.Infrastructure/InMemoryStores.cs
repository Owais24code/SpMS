using System.Collections.Concurrent;
using Spms.Application;
using Spms.Domain.Audit;
using Spms.Domain.Concurrency;
using Spms.Domain.Scheduling;

namespace Spms.Infrastructure;

/// <summary>
/// In-memory persistence so the R1 slice runs end to end today.
///
/// Deliberately behind the same interfaces EF Core will implement, so
/// swapping to PostgreSQL changes registration only. Not durable, not
/// concurrent-safe across nodes — never ship this.
/// </summary>
public sealed class InMemoryAppointmentRepository : IAppointmentRepository
{
    private readonly ConcurrentDictionary<Guid, Appointment> _rows = new();

    public Appointment? Get(Guid id) => _rows.TryGetValue(id, out var a) ? a : null;

    public IReadOnlyList<Appointment> ForProperty(string propertyId, DateOnly day) =>
        _rows.Values
            .Where(a => a.PropertyId == propertyId
                     && DateOnly.FromDateTime(a.StartUtc.UtcDateTime) == day)
            .OrderBy(a => a.StartUtc)
            .ToList();

    public void Upsert(Appointment appointment) => _rows[appointment.AppointmentId] = appointment;

    public int Count => _rows.Count;
}

public sealed class InMemoryAuditSink : IAuditSink
{
    private readonly ConcurrentQueue<AuditEvent> _events = new();

    public void Write(AuditEvent e)
    {
        _events.Enqueue(e);
        while (_events.Count > 500) _events.TryDequeue(out _);
    }

    public IReadOnlyList<AuditEvent> Recent(int take = 50) =>
        _events.Reverse().Take(take).ToList();
}

public sealed class InMemoryIdempotencyStore : IIdempotencyStore
{
    private readonly ConcurrentDictionary<string, IdempotencyRecord> _rows = new();

    private static string Compose(string tenantId, string operation, string key) =>
        $"{tenantId}|{operation}|{key}";

    public bool TryGet(string tenantId, string operation, string key, out IdempotencyRecord record)
        => _rows.TryGetValue(Compose(tenantId, operation, key), out record!);

    public void Put(IdempotencyRecord record)
        => _rows[Compose(record.TenantId, record.Operation, record.Key)] = record;
}
