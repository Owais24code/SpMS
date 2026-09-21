using Spms.Domain.Scheduling;

namespace Spms.Application;

public interface IAppointmentRepository
{
    Appointment? Get(Guid id);
    IReadOnlyList<Appointment> ForProperty(string propertyId, DateOnly day);
    void Upsert(Appointment appointment);
}

/// <summary>Who is calling, and under what authority.</summary>
public sealed record CallerContext(
    string Subject,
    string TenantId,
    string PropertyId,
    IReadOnlySet<string> Scopes,
    string CorrelationId,
    /// <summary>Declared reason for access. Required for restricted reads.</summary>
    string Purpose = "operations")
{
    public bool Has(string scope) => Scopes.Contains(scope);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}
