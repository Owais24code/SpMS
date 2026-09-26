using Spms.Modules.Scheduling.Domain;
using Spms.Modules.Scheduling.Operations;
using Spms.SharedKernel;

namespace Spms.Modules.Scheduling;

/// <summary>Held → Cancelled (HoldExpired) once an online hold runs out, freeing the slot.</summary>
public sealed class HoldExpiryJob(SchedulingService scheduling, ExecutionScope scope) : IPropertyJob
{
    public string Name => "scheduling.hold-expiry";
    public TimeSpan Interval => TimeSpan.FromSeconds(30);
    public Task<int> RunAsync(CancellationToken ct) =>
        scheduling.ReleaseExpiredHoldsAsync(scope.RequireTenant().ToString(), scope.RequireProperty().ToString(), 100, ct);
}

/// <summary>Open preflight tokens past their TTL become Expired.</summary>
public sealed class PreflightExpiryJob(IPreflightStore preflights, IClock clock) : IPropertyJob
{
    public string Name => "scheduling.preflight-expiry";
    public TimeSpan Interval => TimeSpan.FromMinutes(5);
    public Task<int> RunAsync(CancellationToken ct) => preflights.EvictExpiredAsync(clock.UtcNow, ct);
}

/// <summary>Lapsed waitlist offers go back to Waiting; entries past their expiry are Expired.</summary>
public sealed class WaitlistExpiryJob(WaitlistService waitlist) : IPropertyJob
{
    public string Name => "scheduling.waitlist-expiry";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    public Task<int> RunAsync(CancellationToken ct) => waitlist.ExpireAsync(ct);
}
