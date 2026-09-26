using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Core.Data;
using Spms.Modules.Guest.Data;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Modules.Guest.Profiles;

/// <summary>
/// A guest profile pushed from Marquee (Marquee-integrated mode, MCI-004): the
/// first time it creates the SpMS guest and the external mapping; afterwards
/// it updates the names on the mapped guest. Contact details arrive only on
/// creation, where they are stored encrypted like any other.
/// </summary>
public sealed class MarqueeGuestHandler(SpmsDbContext db, GuestProfileService guests, IClock clock) : IInboundHandler
{
    public IReadOnlyCollection<string> EventTypes { get; } = ["marquee.guest.upserted"];

    public async Task<string> HandleAsync(string sourceSystem, string eventType, JsonElement payload, CancellationToken ct)
    {
        string? S(string name) => payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        var key = S("guestKey");
        if (string.IsNullOrWhiteSpace(key)) throw new InboundRejected("guestKey is required.");
        var system = sourceSystem == "marquee" ? "Marquee" : sourceSystem == "pms" ? "Pms" : "External";
        var mapping = await db.Set<ExternalMappingRow>().SingleOrDefaultAsync(m => m.EntityType == "Guest" && m.SourceSystem == system && m.SourceKey == key && m.Status == "Active", ct);
        if (mapping is not null)
        {
            var g = await db.Set<GuestRow>().SingleOrDefaultAsync(x => x.GuestId == mapping.LocalId, ct) ?? throw new InboundRejected("The mapped guest no longer exists.");
            if (S("firstName") is { } f) g.LegalFirstName = f;
            if (S("lastName") is { } l) g.LegalLastName = l;
            mapping.SourceVersion = (mapping.SourceVersion ?? 0) + 1;
            await db.SaveChangesAsync(ct);
            return $"Updated guest {g.GuestId}.";
        }
        var created = await guests.CreateAsync(new NewGuest(S("firstName"), S("lastName"), null, null, S("locale"), null, S("email"), S("mobile"),
            ContactsVerifiedInPerson: false), ct);
        if (created.Outcome != GuestProfileService.Outcome.Ok) throw new InboundRejected(created.Detail ?? "The guest was refused.");
        var guestId = created.View!.Guest.GuestId;
        db.Add(new ExternalMappingRow
        {
            ExternalMappingId = Uuid7.New(), EntityType = "Guest", LocalId = guestId, SourceSystem = system, SourceKey = key,
            ExternalPropertyReference = S("propertyReference"), SourceVersion = 1, EffectiveFrom = clock.UtcNow,
        });
        await db.SaveChangesAsync(ct);
        return $"Created guest {guestId}.";
    }
}
