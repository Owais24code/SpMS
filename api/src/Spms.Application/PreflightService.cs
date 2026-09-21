using System.Collections.Concurrent;
using Spms.Domain.Scheduling;

namespace Spms.Application;

public sealed record MoveProposal(
    Guid AppointmentId,
    string ProviderId,
    string RoomId,
    DateTimeOffset StartUtc,
    int DurationMinutes,
    int FromVersion);

public sealed record PreflightToken(
    string Token,
    DateTimeOffset ExpiresAtUtc,
    MoveProposal Proposal,
    IReadOnlyList<Conflict> Conflicts)
{
    public bool CommitAllowed => Conflicts.All(c => c.Overridable);
}

/// <summary>
/// SCH-020. A move is validated and quoted before it is applied.
///
/// The token exists so the commit is evaluated against the exact state the
/// operator was shown. Re-validating at commit time instead would let the
/// board change between the decision and the write, which is the failure the
/// spec is guarding against.
/// </summary>
public sealed class PreflightService(IAppointmentRepository repo, IClock clock)
{
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(90);

    private readonly ConcurrentDictionary<string, PreflightToken> _issued = new();

    public PreflightToken Evaluate(MoveProposal proposal, CallerContext caller)
    {
        var sameDay = repo.ForProperty(caller.PropertyId, DateOnly.FromDateTime(proposal.StartUtc.UtcDateTime));
        var proposedEnd = proposal.StartUtc.AddMinutes(proposal.DurationMinutes);

        var conflicts = new List<Conflict>();

        // CON-005 requires evaluating the COMPLETE post-change state, so the
        // appointment being moved is excluded and everything else considered.
        foreach (var other in sameDay.Where(a => a.AppointmentId != proposal.AppointmentId))
        {
            if (other.Status is AppointmentStatus.Cancelled or AppointmentStatus.NoShow) continue;

            var overlaps = proposal.StartUtc < other.EndUtc && other.StartUtc < proposedEnd;
            if (!overlaps) continue;

            if (other.RoomId == proposal.RoomId)
                conflicts.Add(ConflictCatalog.ResourceOverlap(proposal.RoomId));

            if (other.ProviderId == proposal.ProviderId)
                conflicts.Add(ConflictCatalog.ProviderOverlap(proposal.ProviderId));
        }

        var token = new PreflightToken(
            Token: "pf_" + Guid.NewGuid().ToString("n")[..12],
            ExpiresAtUtc: clock.UtcNow.Add(Ttl),
            Proposal: proposal,
            Conflicts: conflicts);

        _issued[token.Token] = token;
        return token;
    }

    /// <summary>Single use: redeeming removes it, so a token cannot be replayed.</summary>
    public PreflightToken? Redeem(string token)
    {
        if (!_issued.TryRemove(token, out var found)) return null;
        return found.ExpiresAtUtc <= clock.UtcNow ? null : found;
    }

    public bool IsKnown(string token) => _issued.ContainsKey(token);
}
