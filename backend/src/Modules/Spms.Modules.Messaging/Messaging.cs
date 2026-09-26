using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Spms.Modules.Catalog.Data;
using Spms.Modules.Core.Data;
using Spms.Modules.Core.Infrastructure;
using Spms.Modules.Guest.Data;
using Spms.Modules.Guest.Identity;
using Spms.Modules.Guest.Profiles;
using Spms.Modules.Messaging.Data;
using Spms.Modules.Scheduling.Data;
using Spms.Persistence;
using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Modules.Messaging;

public sealed record TemplateInput(string? TemplateCode, string? Channel, string? Locale, string? Purpose, string? Subject, string? BodyTemplate,
    string? TriggerEvent, int? OffsetMinutes, Guid? ServiceId, bool? RespectQuietHours, bool? PropertyOnly);
public sealed record SendInput(Guid? AppointmentId, string? TemplateCode, string? Channel);
public sealed record CallbackInput(string? ProviderMessageId, string? Status, string? FailureCode);

public enum SendOutcome { Accepted, TransientFailure, Rejected, Bounced }
public sealed record SendResult(SendOutcome Outcome, string? ProviderMessageId, string? FailureCode = null);

/// <summary>
/// The message provider, behind a port (DEC-008 is open). Every send carries
/// the message's own idempotency key, so a retried dispatch is the same send.
/// </summary>
public interface IMessageSender
{
    string ProviderCode { get; }
    Task<SendResult> SendAsync(string channel, string address, string? subject, string body, string idempotencyKey, CancellationToken ct);
}

/// <summary>
/// The development provider. An address containing "bounce" bounces, "fail"
/// fails transiently, "reject" is refused. Everything sent is kept in memory
/// (newest first) so a developer can see what would have gone out.
/// </summary>
public sealed class SimulatedMessageSender : IMessageSender
{
    public sealed record Sent(string Channel, string Address, string? Subject, string Body, string ProviderMessageId, DateTimeOffset At);

    private readonly ConcurrentDictionary<string, SendResult> _byKey = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<Sent> _outbox = new();

    public string ProviderCode => "sim";

    public IReadOnlyList<Sent> Recent => _outbox.Reverse().Take(100).ToList();

    public Task<SendResult> SendAsync(string channel, string address, string? subject, string body, string idempotencyKey, CancellationToken ct)
    {
        var result = _byKey.GetOrAdd(idempotencyKey, _ =>
        {
            if (address.Contains("reject", StringComparison.OrdinalIgnoreCase)) return new SendResult(SendOutcome.Rejected, null, "InvalidRecipient");
            if (address.Contains("fail", StringComparison.OrdinalIgnoreCase)) return new SendResult(SendOutcome.TransientFailure, null, "ProviderUnavailable");
            var id = "sim_" + Guid.NewGuid().ToString("N")[..16];
            _outbox.Enqueue(new Sent(channel, address, subject, body, id, DateTimeOffset.UtcNow));
            while (_outbox.Count > 500) _outbox.TryDequeue(out var _);
            return new SendResult(address.Contains("bounce", StringComparison.OrdinalIgnoreCase) ? SendOutcome.Bounced : SendOutcome.Accepted, id,
                address.Contains("bounce", StringComparison.OrdinalIgnoreCase) ? "Bounced" : null);
        });
        // A transient failure is not remembered: the retry is a real retry.
        if (result.Outcome == SendOutcome.TransientFailure) _byKey.TryRemove(idempotencyKey, out _);
        return Task.FromResult(result);
    }
}

public sealed record QuietHours(string Start = "21:00", string End = "08:00");

/// <summary>
/// Guest messaging (§53.2; SEC-010; DEC-008). Templates are versioned and
/// approved by someone other than their author; only the variables on the
/// allow-list render, so a template can never carry intake answers, notes or
/// payment details. Reminders are scheduled from the board by a job and
/// dispatched by another; a message is sent once per template, channel and
/// appointment whatever version is current, respects quiet hours in the
/// property's own zone, and a marketing message needs the guest's consent.
/// </summary>
public sealed partial class MessagingService(
    SpmsDbContext db, MasterData master, IFieldProtector protector, ConsentService consent, SettingsReader settings,
    IMessageSender sender, IUnitOfWork uow, IClock clock)
{
    public static readonly string[] Channels = ["Email", "Sms", "WhatsApp", "Push"];
    public static readonly string[] Purposes = ["Confirmation", "Reminder", "Cancellation", "IntakeRequest", "Receipt", "Waitlist", "Marketing", "Transactional"];
    public static readonly string[] Triggers = ["AppointmentConfirmed", "BeforeStart", "AfterCompletion", "IntakeDue", "WaitlistOffer"];
    public static readonly string[] Variables = ["guestName", "serviceName", "startLocal", "propertyName", "confirmationNumber"];
    private const string RecipientPurpose = "messaging.scheduled_message.recipient";
    private const int MaxAttempts = 3;

    [GeneratedRegex(@"\{\{\s*([A-Za-z0-9_.]+)\s*\}\}")] private static partial Regex Placeholder();

    public enum Outcome { Ok, NotFound, Invalid, Illegal, StaleVersion, SameApprover, NoRecipient, NoConsent }

    /* ------------------------------- templates ------------------------------- */

    /// <summary>SEC-010: a placeholder not on the allow-list is refused, and with it anything clinical or financial.</summary>
    public static string? CheckTemplate(TemplateInput i)
    {
        if (i.TemplateCode is null || !Regex.IsMatch(i.TemplateCode, "^[a-z][a-z0-9-]{1,39}$")) return "templateCode is 2–40 lowercase letters, digits and hyphens.";
        if (!Channels.Contains(i.Channel)) return "channel is Email, Sms, WhatsApp or Push.";
        if (!Purposes.Contains(i.Purpose)) return $"purpose is one of {string.Join(", ", Purposes)}.";
        if (string.IsNullOrWhiteSpace(i.BodyTemplate) || i.BodyTemplate.Length > 4000) return "bodyTemplate is 1–4000 characters.";
        if (i.Channel == "Email" && string.IsNullOrWhiteSpace(i.Subject)) return "An email has a subject.";
        if ((i.TriggerEvent is null) != (i.OffsetMinutes is null)) return "A trigger has an offset, and an offset a trigger.";
        if (i.TriggerEvent is not null && !Triggers.Contains(i.TriggerEvent)) return $"triggerEvent is one of {string.Join(", ", Triggers)}.";
        if (i.OffsetMinutes is < -43_200 or > 43_200) return "offsetMinutes is within 30 days of the event.";
        var unknown = Placeholder().Matches((i.Subject ?? "") + " " + i.BodyTemplate).Select(m => m.Groups[1].Value).Where(v => !Variables.Contains(v)).Distinct().ToList();
        if (unknown.Count > 0) return $"Unknown variables {string.Join(", ", unknown)}: a message may use only {string.Join(", ", Variables)}.";
        return null;
    }

    public Task<List<MessageTemplateRow>> TemplatesAsync(string? code, CancellationToken ct) =>
        db.Set<MessageTemplateRow>().AsNoTracking().Where(t => code == null || t.TemplateCode == code)
            .OrderBy(t => t.TemplateCode).ThenBy(t => t.Channel).ThenByDescending(t => t.VersionNumber).ToListAsync(ct);

    public async Task<Edit<MessageTemplateRow>> DraftAsync(TemplateInput i, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        Guid? property = i.PropertyOnly == true ? db.Scope.RequireProperty() : null;
        var locale = i.Locale ?? "en-US";
        var last = await db.Set<MessageTemplateRow>().Where(t => t.TemplateCode == i.TemplateCode && t.Channel == i.Channel && t.Locale == locale && t.PropertyId == property)
            .MaxAsync(t => (int?)t.VersionNumber, ct) ?? 0;
        var body = i.BodyTemplate!;
        var row = new MessageTemplateRow
        {
            MessageTemplateId = Uuid7.New(), PropertyId = property, TemplateCode = i.TemplateCode!, VersionNumber = last + 1, Channel = i.Channel!,
            Locale = locale, Purpose = i.Purpose!, Subject = i.Subject, BodyTemplate = body,
            Variables = Placeholder().Matches((i.Subject ?? "") + " " + body).Select(m => m.Groups[1].Value).Distinct().ToArray(),
            TriggerEvent = i.TriggerEvent, OffsetMinutes = i.OffsetMinutes, ServiceId = i.ServiceId, RespectQuietHours = i.RespectQuietHours ?? true,
            Status = "Draft", EffectiveFrom = clock.UtcNow,
        };
        var r = await master.CreateAsync(row, "messaging.template.draft", "message_template", t => t.MessageTemplateId, ct);
        if (r.Outcome == EditOutcome.Ok) await tx.CommitAsync(ct);
        return r;
    }

    /// <summary>Approved by someone other than its author; it becomes the Active version and the previous one retires.</summary>
    public async Task<(Outcome Outcome, MessageTemplateRow? Row, string? Detail)> ApproveAsync(Guid id, int version, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var t = await db.Set<MessageTemplateRow>().SingleOrDefaultAsync(x => x.MessageTemplateId == id, ct);
        if (t is null) return (Outcome.NotFound, null, null);
        if (t.Version != version) { db.ChangeTracker.Clear(); return (Outcome.StaleVersion, t, null); }
        if (t.Status != "Draft") { db.ChangeTracker.Clear(); return (Outcome.Illegal, t, $"A {t.Status} template has been decided."); }
        if (t.CreatedBy is { } author && author == db.Scope.PrincipalId) { db.ChangeTracker.Clear(); return (Outcome.SameApprover, t, "The author of a template cannot approve it."); }
        var now = clock.UtcNow;
        var previous = await db.Set<MessageTemplateRow>().Where(x => x.MessageTemplateId != id && x.TemplateCode == t.TemplateCode && x.Channel == t.Channel
            && x.Locale == t.Locale && x.PropertyId == t.PropertyId && (x.Status == "Active" || x.Status == "Approved")).ToListAsync(ct);
        foreach (var p in previous) { p.Status = "Retired"; p.EffectiveTo = now; }
        t.Status = "Active";
        t.ApprovedBy = db.Scope.PrincipalId;
        t.ApprovedAt = now;
        t.EffectiveFrom = now;
        await db.SaveChangesAsync(ct);
        await master.AuditAsync(new AuditEntry("messaging.template.approve", "message_template", id.ToString(), t.Version, FromStatus: "Draft", ToStatus: "Active",
            AfterData: new { t.TemplateCode, t.Channel, t.VersionNumber, retired = previous.Select(p => p.VersionNumber) }), ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return (Outcome.Ok, t, null);
    }

    public Task<Edit<MessageTemplateRow>> RetireAsync(Guid id, int version, CancellationToken ct) =>
        master.ChangeAsync<MessageTemplateRow>(t => t.MessageTemplateId == id, version, "messaging.template.retire", "message_template", t => t.MessageTemplateId, t =>
        {
            if (t.Status == "Retired") return "Already retired.";
            t.Status = "Retired";
            t.EffectiveTo = clock.UtcNow;
            return null;
        }, ct);

    /* ------------------------------- rendering ------------------------------- */

    private sealed class Context
    {
        public Guid AppointmentId { get; set; }
        public Guid GuestId { get; set; }
        public Guid ServiceId { get; set; }
        public string Status { get; set; } = "";
        public DateTimeOffset StartAt { get; set; }
        public DateTimeOffset EndAt { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public string? ConfirmationNumber { get; set; }
        public string ServiceName { get; set; } = "";
        public bool RequiresIntake { get; set; }
        public string GuestAlias { get; set; } = "";
    }

    private IQueryable<Context> Contexts() =>
        from a in db.Set<AppointmentRow>().AsNoTracking()
        join s in db.Set<ServiceRow>() on a.ServiceId equals s.ServiceId
        join g in db.Set<GuestRow>() on a.GuestId equals g.GuestId
        select new Context
        {
            AppointmentId = a.AppointmentId, GuestId = a.GuestId, ServiceId = a.ServiceId, Status = a.Status, StartAt = a.StartAt, EndAt = a.EndAt,
            CreatedAt = a.CreatedAt, CompletedAt = a.CompletedAt, ConfirmationNumber = a.ConfirmationNumber, ServiceName = s.Name,
            RequiresIntake = s.RequiresIntake, GuestAlias = g.DisplayAlias ?? "Guest",
        };

    private async Task<PropertyRow> PropertyAsync(CancellationToken ct)
    {
        var id = db.Scope.RequireProperty();
        return await db.Set<PropertyRow>().AsNoTracking().SingleAsync(p => p.PropertyId == id, ct);
    }

    private static string Render(string template, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(template, m => values.TryGetValue(m.Groups[1].Value, out var v) ? v : "");

    private static Dictionary<string, string> Values(Context c, PropertyRow p)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);
        var local = TimeZoneInfo.ConvertTime(c.StartAt, zone);
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["guestName"] = c.GuestAlias, ["serviceName"] = c.ServiceName, ["propertyName"] = p.Name,
            ["startLocal"] = local.ToString("dddd d MMMM, h:mm tt", CultureInfo.GetCultureInfo(p.Locale)),
            ["confirmationNumber"] = c.ConfirmationNumber ?? "",
        };
    }

    public async Task<(string? Subject, string Body)?> PreviewAsync(Guid templateId, Guid appointmentId, CancellationToken ct)
    {
        var t = await db.Set<MessageTemplateRow>().AsNoTracking().SingleOrDefaultAsync(x => x.MessageTemplateId == templateId, ct);
        var c = await Contexts().SingleOrDefaultAsync(x => x.AppointmentId == appointmentId, ct);
        if (t is null || c is null) return null;
        var values = Values(c, await PropertyAsync(ct));
        return (t.Subject is null ? null : Render(t.Subject, values), Render(t.BodyTemplate, values));
    }

    /* ------------------------------ scheduling ------------------------------- */

    private async Task<(string Address, Guid ContactId)?> RecipientAsync(Guid guestId, string channel, CancellationToken ct)
    {
        var type = channel == "Email" ? "Email" : "Mobile";
        var point = await db.Set<GuestContactPointRow>().AsNoTracking()
            .Where(c => c.GuestId == guestId && c.ContactType == type && c.Status == "Active" && c.SuppressionReason == null)
            .OrderByDescending(c => c.IsPrimary).ThenByDescending(c => c.VerifiedAt).FirstOrDefaultAsync(ct);
        if (point is null) return null;
        var value = Encoding.UTF8.GetString(protector.Unprotect(point.ContactCipher, point.KeyVersion, ContactValues.CipherPurpose));
        return (value, point.GuestContactPointId);
    }

    /// <summary>Moves a send time out of the property's quiet hours to the end of them.</summary>
    private async Task<DateTimeOffset> OutOfQuietHoursAsync(DateTimeOffset at, PropertyRow p, CancellationToken ct)
    {
        var quiet = await settings.GetAsync("messaging.quiet_hours", new QuietHours(), ct);
        if (!TimeOnly.TryParse(quiet.Start, out var start) || !TimeOnly.TryParse(quiet.End, out var end) || start == end) return at;
        var zone = TimeZoneInfo.FindSystemTimeZoneById(p.Timezone);
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var t = TimeOnly.FromDateTime(local.DateTime);
        var inQuiet = start < end ? t >= start && t < end : t >= start || t < end;
        if (!inQuiet) return at;
        var endDay = DateOnly.FromDateTime(local.DateTime);
        if (start > end && t >= start) endDay = endDay.AddDays(1);
        var endLocal = endDay.ToDateTime(end);
        return new DateTimeOffset(endLocal, zone.GetUtcOffset(endLocal)).ToUniversalTime();
    }

    private async Task<(Outcome Outcome, ScheduledMessageRow? Row)> EnqueueAsync(MessageTemplateRow t, Context c, DateTimeOffset sendAfter, DateTimeOffset? expires,
        string key, PropertyRow p, CancellationToken ct, bool quietHours = true)
    {
        if (await db.Set<ScheduledMessageRow>().AsNoTracking().AnyAsync(m => m.IdempotencyKey == key, ct)) return (Outcome.Ok, null);
        Guid? consentId = null;
        if (t.Purpose == "Marketing")
        {
            if (!await consent.HasActiveAsync(c.GuestId, "Marketing", t.Channel, ct)) return (Outcome.NoConsent, null);
            consentId = await db.Set<ConsentRecordRow>().AsNoTracking().Where(r => r.GuestId == c.GuestId && r.Purpose == "Marketing" && r.Status == "Active")
                .Select(r => (Guid?)r.ConsentId).FirstOrDefaultAsync(ct);
        }
        if (await RecipientAsync(c.GuestId, t.Channel, ct) is not { } recipient) return (Outcome.NoRecipient, null);
        var id = Uuid7.New();
        var sealed_ = protector.Protect(Encoding.UTF8.GetBytes(recipient.Address), RecipientPurpose);
        if (quietHours && t.RespectQuietHours) sendAfter = await OutOfQuietHoursAsync(sendAfter, p, ct);
        if (expires is { } e && sendAfter >= e) return (Outcome.Illegal, null);
        var row = new ScheduledMessageRow
        {
            ScheduledMessageId = id, PropertyId = p.PropertyId, GuestId = c.GuestId, AppointmentId = c.AppointmentId, MessageTemplateId = t.MessageTemplateId,
            ConsentRecordId = consentId, Channel = t.Channel, RecipientAddressCipher = sealed_.Cipher, KeyVersion = sealed_.KeyVersion,
            SendAfter = sendAfter, ExpiresAt = expires, ProviderCode = sender.ProviderCode, IdempotencyKey = key, Status = "Scheduled",
        };
        db.Add(row);
        await db.SaveChangesAsync(ct);
        return (Outcome.Ok, row);
    }

    /// <summary>
    /// The reminder job: every Active template with a trigger, against the
    /// appointments it applies to, once per template code, channel and
    /// appointment. Messages for bookings that were cancelled are cancelled.
    /// </summary>
    public async Task<int> ScheduleDueAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var p = await PropertyAsync(ct);
        var property = p.PropertyId;
        var templates = await db.Set<MessageTemplateRow>().AsNoTracking()
            .Where(t => t.Status == "Active" && t.TriggerEvent != null && (t.PropertyId == null || t.PropertyId == property)).ToListAsync(ct);
        // A property's own version of a template wins over the tenant's.
        templates = templates.GroupBy(t => (t.TemplateCode, t.Channel)).Select(g => g.OrderByDescending(t => t.PropertyId != null).First()).ToList();
        var made = 0;
        foreach (var t in templates)
        {
            var offset = TimeSpan.FromMinutes(t.OffsetMinutes ?? 0);
            var q = Contexts().Where(c => t.ServiceId == null || c.ServiceId == t.ServiceId);
            List<Context> targets = t.TriggerEvent switch
            {
                "AppointmentConfirmed" => await q.Where(c => c.Status == "Confirmed" && c.CreatedAt > now.AddDays(-2) && c.StartAt > now).ToListAsync(ct),
                "BeforeStart" => await q.Where(c => c.Status == "Confirmed" && c.StartAt > now && c.StartAt < now.AddDays(8)).ToListAsync(ct),
                "IntakeDue" => await q.Where(c => c.Status == "Confirmed" && c.RequiresIntake && c.StartAt > now && c.StartAt < now.AddDays(8)).ToListAsync(ct),
                "AfterCompletion" => await q.Where(c => c.Status == "Completed" && c.CompletedAt != null && c.CompletedAt > now.AddDays(-2)).ToListAsync(ct),
                _ => [],
            };
            foreach (var c in targets)
            {
                var (at, expires) = t.TriggerEvent switch
                {
                    "AppointmentConfirmed" => (c.CreatedAt + offset, (DateTimeOffset?)c.StartAt),
                    "AfterCompletion" => (c.CompletedAt!.Value + offset, c.CompletedAt.Value.AddDays(3)),
                    _ => (c.StartAt + offset, c.StartAt),
                };
                if (at < now) at = now;
                var (outcome, row) = await EnqueueAsync(t, c, at, expires, $"auto:{t.TemplateCode}:{t.Channel}:{c.AppointmentId:N}", p, ct);
                if (outcome == Outcome.Ok && row is not null) made++;
            }
        }
        var stale = await (from m in db.Set<ScheduledMessageRow>()
                           where m.Status == "Scheduled" && m.AppointmentId != null
                           join a in db.Set<AppointmentRow>() on m.AppointmentId equals a.AppointmentId
                           where a.Status == "Cancelled" || a.Status == "NoShow"
                           select m).ToListAsync(ct);
        foreach (var m in stale) { m.Status = "Cancelled"; m.FailureCode = "AppointmentCancelled"; }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return made + stale.Count;
    }

    /// <summary>A message from a template to one booking's guest now (the desk's "send the intake link again").</summary>
    public async Task<(Outcome Outcome, ScheduledMessageRow? Row)> SendNowAsync(Guid appointmentId, string templateCode, string? channel, string requestKey, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var p = await PropertyAsync(ct);
        var c = await Contexts().SingleOrDefaultAsync(x => x.AppointmentId == appointmentId, ct);
        if (c is null) return (Outcome.NotFound, null);
        var t = await db.Set<MessageTemplateRow>().AsNoTracking()
            .Where(x => x.TemplateCode == templateCode && x.Status == "Active" && (channel == null || x.Channel == channel) && (x.PropertyId == null || x.PropertyId == p.PropertyId))
            .OrderByDescending(x => x.PropertyId != null).FirstOrDefaultAsync(ct);
        if (t is null) return (Outcome.Invalid, null);
        var key = $"manual:{requestKey}";
        var existing = await db.Set<ScheduledMessageRow>().AsNoTracking().SingleOrDefaultAsync(m => m.IdempotencyKey == key, ct);
        if (existing is not null) return (Outcome.Ok, existing);
        // Sent while the guest is at the desk: now, not after quiet hours.
        var (outcome, row) = await EnqueueAsync(t, c, clock.UtcNow, clock.UtcNow.AddDays(1), key, p, ct, quietHours: false);
        if (outcome != Outcome.Ok) return (outcome, null);
        await master.AuditAsync(new AuditEntry("messaging.send", "scheduled_message", row!.ScheduledMessageId.ToString(), 1,
            AfterData: new { t.TemplateCode, t.Channel, appointmentId }), ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return (Outcome.Ok, row);
    }

    /// <summary>
    /// The dispatch job: due messages are rendered and handed to the provider
    /// with their own key. A transient failure is retried with backoff up to
    /// three attempts; a message past its moment (a reminder after the
    /// treatment began) expires instead of going out late.
    /// </summary>
    public async Task<int> DispatchDueAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var due = await db.Set<ScheduledMessageRow>().Where(m => m.Status == "Scheduled" && m.SendAfter <= now).OrderBy(m => m.SendAfter).Take(50).ToListAsync(ct);
        if (due.Count == 0) return 0;
        var p = await PropertyAsync(ct);
        var templates = await db.Set<MessageTemplateRow>().AsNoTracking().Where(t => due.Select(d => d.MessageTemplateId).Contains(t.MessageTemplateId)).ToDictionaryAsync(t => t.MessageTemplateId, ct);
        var ids = due.Where(d => d.AppointmentId != null).Select(d => d.AppointmentId!.Value).ToList();
        var contexts = await Contexts().Where(c => ids.Contains(c.AppointmentId)).ToDictionaryAsync(c => c.AppointmentId, ct);
        foreach (var m in due)
        {
            if (m.ExpiresAt is { } e && e <= now) { m.Status = "Expired"; continue; }
            var t = templates[m.MessageTemplateId];
            var values = m.AppointmentId is { } aid && contexts.TryGetValue(aid, out var c) ? Values(c, p) : new Dictionary<string, string>();
            var address = Encoding.UTF8.GetString(protector.Unprotect(m.RecipientAddressCipher, m.KeyVersion, RecipientPurpose));
            m.AttemptCount++;
            var r = await sender.SendAsync(m.Channel, address, t.Subject is null ? null : Render(t.Subject, values), Render(t.BodyTemplate, values), m.IdempotencyKey, ct);
            Telemetry.Messages.Add(1, new KeyValuePair<string, object?>("channel", m.Channel), new KeyValuePair<string, object?>("outcome", r.Outcome.ToString()));
            switch (r.Outcome)
            {
                case SendOutcome.Accepted:
                    m.Status = "Sent"; m.SentAt = now; m.ProviderMessageId = r.ProviderMessageId; m.FailureCode = null;
                    break;
                case SendOutcome.Bounced:
                    m.Status = "Bounced"; m.SentAt = now; m.ProviderMessageId = r.ProviderMessageId; m.FailureCode = r.FailureCode;
                    break;
                case SendOutcome.TransientFailure when m.AttemptCount < MaxAttempts:
                    m.SendAfter = now.AddMinutes(5 * m.AttemptCount); m.FailureCode = r.FailureCode;
                    break;
                default:
                    m.Status = "Failed"; m.FailureCode = r.FailureCode ?? "Failed";
                    break;
            }
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return due.Count;
    }

    /// <summary>A delivery report from the provider: Delivered, Bounced or Failed. Unknown ids are ignored.</summary>
    public async Task<(Outcome Outcome, ScheduledMessageRow? Row)> CallbackAsync(string providerMessageId, string status, string? failure, CancellationToken ct)
    {
        await using var tx = await uow.BeginAsync(ct);
        var m = await db.Set<ScheduledMessageRow>().SingleOrDefaultAsync(x => x.ProviderMessageId == providerMessageId, ct);
        if (m is null) return (Outcome.NotFound, null);
        if (m.Status is not ("Sent" or "Delivered")) { db.ChangeTracker.Clear(); return (Outcome.Illegal, m); }
        var before = m.Status;
        switch (status)
        {
            case "Delivered": m.Status = "Delivered"; m.DeliveredAt = clock.UtcNow; break;
            case "Bounced": m.Status = "Bounced"; m.DeliveredAt = null; m.FailureCode = failure ?? "Bounced"; break;
            default: m.Status = "Failed"; m.DeliveredAt = null; m.FailureCode = failure ?? "Failed"; break;
        }
        await db.SaveChangesAsync(ct);
        await master.AuditAsync(new AuditEntry("messaging.delivery", "scheduled_message", m.ScheduledMessageId.ToString(), m.Version, FromStatus: before, ToStatus: m.Status,
            ReasonCode: m.FailureCode, PropertyId: m.PropertyId), ct);
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        await tx.CommitAsync(ct);
        return (Outcome.Ok, m);
    }

    public Task<List<ScheduledMessageRow>> MessagesAsync(Guid? appointmentId, Guid? guestId, string? status, CancellationToken ct) =>
        db.Set<ScheduledMessageRow>().AsNoTracking()
            .Where(m => (appointmentId == null || m.AppointmentId == appointmentId) && (guestId == null || m.GuestId == guestId) && (status == null || m.Status == status))
            .OrderByDescending(m => m.SendAfter).Take(200).ToListAsync(ct);

    public Task<Edit<ScheduledMessageRow>> CancelAsync(Guid id, int version, CancellationToken ct) =>
        master.ChangeAsync<ScheduledMessageRow>(m => m.ScheduledMessageId == id, version, "messaging.cancel", "scheduled_message", m => m.ScheduledMessageId, m =>
        {
            if (m.Status != "Scheduled") return $"A {m.Status} message cannot be cancelled.";
            m.Status = "Cancelled";
            return null;
        }, ct, after: m => new { m.ScheduledMessageId, m.Channel, m.Status });
}

public sealed record MessagingRetention(int Days = 180);

/// <summary>
/// Once a message is finished and past retention (setting retention.messaging,
/// 180 days by default), its encrypted address is destroyed; the record that
/// a message of that template went out on that day stays. A held message is
/// left whole.
/// </summary>
public sealed class MessageRetentionJob(SpmsDbContext db, SettingsReader settings, Spms.Modules.Core.Infrastructure.LegalHolds holds, IClock clock) : IPropertyJob
{
    private static readonly string[] Finished = ["Sent", "Delivered", "Failed", "Bounced", "Cancelled", "Expired", "Suppressed"];

    public string Name => "messaging.retention";
    public TimeSpan Interval => TimeSpan.FromHours(6);

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var policy = await settings.GetAsync("retention.messaging", new MessagingRetention(), ct);
        var cutoff = clock.UtcNow.AddDays(-Math.Max(1, policy.Days));
        var held = await holds.HeldKeysAsync("messaging.scheduled_message", ct);
        var due = await db.Set<ScheduledMessageRow>().Where(m => Finished.Contains(m.Status) && m.SendAfter < cutoff && m.KeyVersion != "purged")
            .Take(500).ToListAsync(ct);
        var n = 0;
        foreach (var m in due.Where(m => !held.Contains(m.ScheduledMessageId.ToString())))
        {
            m.RecipientAddressCipher = [];
            m.KeyVersion = "purged";
            n++;
        }
        await db.SaveChangesAsync(ct);
        db.ChangeTracker.Clear();
        return n;
    }
}

public sealed class ReminderJob(MessagingService messaging) : IPropertyJob
{
    public string Name => "messaging.schedule-reminders";
    public TimeSpan Interval => TimeSpan.FromMinutes(10);
    public Task<int> RunAsync(CancellationToken ct) => messaging.ScheduleDueAsync(ct);
}

public sealed class DispatchJob(MessagingService messaging) : IPropertyJob
{
    public string Name => "messaging.dispatch";
    public TimeSpan Interval => TimeSpan.FromMinutes(1);
    public Task<int> RunAsync(CancellationToken ct) => messaging.DispatchDueAsync(ct);
}

public static class MessagingEndpoints
{
    private static object Template(MessageTemplateRow t) => new
    {
        templateId = t.MessageTemplateId, t.TemplateCode, t.VersionNumber, t.Channel, t.Locale, t.Purpose, t.Subject, t.BodyTemplate, t.Variables,
        t.TriggerEvent, t.OffsetMinutes, t.ServiceId, t.RespectQuietHours, propertyOnly = t.PropertyId != null, t.Status, authoredBy = t.CreatedBy,
        t.ApprovedBy, rowVersion = t.Version, eTag = $"\"{t.Version}\"",
    };

    /// <summary>Never the address: the recipient is shown by channel only.</summary>
    private static object Message(ScheduledMessageRow m) => new
    {
        messageId = m.ScheduledMessageId, m.GuestId, m.AppointmentId, templateId = m.MessageTemplateId, m.Channel,
        sendAfterUtc = m.SendAfter.ToUniversalTime().ToString("O"), expiresUtc = m.ExpiresAt?.ToUniversalTime().ToString("O"),
        sentUtc = m.SentAt?.ToUniversalTime().ToString("O"), deliveredUtc = m.DeliveredAt?.ToUniversalTime().ToString("O"),
        m.AttemptCount, m.FailureCode, m.ProviderCode, m.ProviderMessageId, m.Status, rowVersion = m.Version, eTag = $"\"{m.Version}\"",
    };

    /// <summary>Reading and sending ride on ordinary access; OpenFGA decides who. Writing templates is messaging or admin work.</summary>
    private static Task<IResult?> Can(RequestContext ctx, IAccessDecider access, string relation, CancellationToken ct)
    {
        var authoring = relation is "can_manage_templates" or "can_approve_templates";
        var scope = !authoring ? SpaScopes.Read : ctx.Has(SpaScopes.Admin) ? SpaScopes.Admin : SpaScopes.Messaging;
        return WebApi.GateAsync(ctx, access, scope, relation, Fga.Property(ctx.PropertyId), ct);
    }

    public static IEndpointRouteBuilder MapMessaging(this IEndpointRouteBuilder app)
    {
        app.MapGet("/messaging/templates", async (HttpContext http, MessagingService svc, IAccessDecider access, string? code, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_read_messages", ct) is { } refused) return refused;
            return Results.Json((await svc.TemplatesAsync(code, ct)).Select(Template), Json.Options);
        });

        app.MapPost("/messaging/templates", async (HttpContext http, MessagingService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_manage_templates", ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<TemplateInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (MessagingService.CheckTemplate(i) is { } bad) return WebApi.Invalid(ctx, bad);
            return (await svc.DraftAsync(i, ct)).ToHttp(http, ctx, Template, 201);
        });

        app.MapPost("/messaging/templates/{id:guid}/approve", async (HttpContext http, MessagingService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_approve_templates", ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            var r = await svc.ApproveAsync(id, version, ct);
            return r.Outcome switch
            {
                MessagingService.Outcome.Ok => EditResults.Ok(http, r.Row!, Template),
                MessagingService.Outcome.NotFound => WebApi.NotFound(ctx),
                MessagingService.Outcome.StaleVersion => Problem.From(ApiError.StaleVersion, ctx.CorrelationId, extensions: Problem.Ext("current", Template(r.Row!))),
                MessagingService.Outcome.SameApprover => Problem.From(ApiError.AuthorizationDenied, ctx.CorrelationId, r.Detail),
                _ => Problem.From(ApiError.HardConflict, ctx.CorrelationId, r.Detail),
            };
        });

        app.MapPost("/messaging/templates/{id:guid}/retire", async (HttpContext http, MessagingService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_manage_templates", ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            return (await svc.RetireAsync(id, version, ct)).ToHttp(http, ctx, Template);
        });

        app.MapGet("/messaging/templates/{id:guid}/preview", async (HttpContext http, MessagingService svc, IAccessDecider access, Guid id, Guid appointmentId, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_read_messages", ct) is { } refused) return refused;
            var r = await svc.PreviewAsync(id, appointmentId, ct);
            return r is { } v ? Results.Json(new { subject = v.Subject, body = v.Body }, Json.Options) : WebApi.NotFound(ctx);
        });

        app.MapGet("/messaging/messages", async (HttpContext http, MessagingService svc, IAccessDecider access, Guid? appointmentId, Guid? guestId, string? status, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_read_messages", ct) is { } refused) return refused;
            return Results.Json((await svc.MessagesAsync(appointmentId, guestId, status, ct)).Select(Message), Json.Options);
        });

        app.MapPost("/messaging/messages", async (HttpContext http, MessagingService svc, IAccessDecider access, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_send_messages", ct) is { } refused) return refused;
            if (http.Request.Headers["Idempotency-Key"].FirstOrDefault() is not { Length: >= 8 and <= 200 } key)
                return WebApi.Invalid(ctx, "Idempotency-Key (8–200 characters) is required to send a message.");
            var (i, fail) = await WebApi.BodyAsync<SendInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (i.AppointmentId is not { } appointment || string.IsNullOrWhiteSpace(i.TemplateCode)) return WebApi.Invalid(ctx, "appointmentId and templateCode are required.");
            var r = await svc.SendNowAsync(appointment, i.TemplateCode, i.Channel, key, ct);
            return r.Outcome switch
            {
                MessagingService.Outcome.Ok => Results.Json(Message(r.Row!), Json.Options, statusCode: 201),
                MessagingService.Outcome.NotFound => WebApi.NotFound(ctx),
                MessagingService.Outcome.NoRecipient => Problem.From(ApiError.HardConflict, ctx.CorrelationId, "The guest has no usable address on that channel."),
                MessagingService.Outcome.NoConsent => Problem.From(ApiError.HardConflict, ctx.CorrelationId, "The guest has not consented to marketing on that channel."),
                _ => WebApi.Invalid(ctx, "No active template with that code."),
            };
        });

        app.MapPost("/messaging/messages/{id:guid}/cancel", async (HttpContext http, MessagingService svc, IAccessDecider access, Guid id, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await Can(ctx, access, "can_send_messages", ct) is { } refused) return refused;
            if (Guard.RequireIfMatch(http, ctx, out var version) is { } noMatch) return noMatch;
            return (await svc.CancelAsync(id, version, ct)).ToHttp(http, ctx, Message);
        });

        // Delivery reports: the provider's webhook, authenticated as the integration service.
        app.MapPost("/messaging/callbacks/{provider}", async (HttpContext http, MessagingService svc, IAccessDecider access, string provider, CancellationToken ct) =>
        {
            var ctx = RequestContext.From(http);
            if (await WebApi.GateAsync(ctx, access, SpaScopes.Write, "can_integrate", Fga.Property(ctx.PropertyId), ct) is { } refused) return refused;
            var (i, fail) = await WebApi.BodyAsync<CallbackInput>(http, ctx, ct);
            if (i is null) return fail!;
            if (string.IsNullOrWhiteSpace(i.ProviderMessageId) || i.Status is not ("Delivered" or "Bounced" or "Failed"))
                return WebApi.Invalid(ctx, "providerMessageId and status (Delivered, Bounced, Failed) are required.");
            var r = await svc.CallbackAsync(i.ProviderMessageId, i.Status, i.FailureCode, ct);
            // A report for a message we do not know is acknowledged, not retried forever by the provider.
            return r.Outcome is MessagingService.Outcome.Ok or MessagingService.Outcome.NotFound or MessagingService.Outcome.Illegal
                ? Results.Json(new { accepted = r.Outcome == MessagingService.Outcome.Ok }, Json.Options)
                : WebApi.Invalid(ctx, "Refused.");
        });

        // Development only: what the simulated provider would have sent.
        app.MapGet("/dev/messages", (HttpContext http, IMessageSender sender, Microsoft.Extensions.Hosting.IHostEnvironment env) =>
        {
            if (!Microsoft.Extensions.Hosting.HostEnvironmentEnvExtensions.IsDevelopment(env) || sender is not SimulatedMessageSender sim) return Results.NotFound();
            return Results.Json(sim.Recent, Json.Options);
        });

        return app;
    }
}
