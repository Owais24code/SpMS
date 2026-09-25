using Microsoft.EntityFrameworkCore;
using Spms.Domain.Scheduling;
using Spms.Infrastructure.Postgres.Rows;

namespace Spms.Infrastructure.Postgres;

/// <summary>
/// The EF Core context.
///
/// It maps PERSISTENCE ROW TYPES, not the domain aggregate. That is a
/// deliberate choice with a cost and a reason:
///
///   cost   — the mapping between row and aggregate is written by hand
///            (AppointmentMap), and a new field has to be added in both.
///   reason — the domain stays free of EF entirely, the aggregate keeps its
///            private setters and factories instead of being reshaped to suit
///            a materializer, and the repository can go on returning
///            independent instances rather than entities from EF's identity
///            map. The 412 path depends on that: it re-reads to show the
///            client the real stored state, and a tracked entity would hand
///            back the same already-mutated instance and report the rejected
///            proposal as if it were stored.
///
/// The schema itself is owned by the SQL files in Migrations/, not by EF.
/// EF never creates or alters a table here — <see cref="SchemaGuard"/> checks
/// the two agree, so a mapping that drifts from the migration fails loudly at
/// startup instead of at the first query.
/// </summary>
public sealed class SpmsDbContext(DbContextOptions<SpmsDbContext> options) : DbContext(options)
{
    public DbSet<AppointmentRow> Appointments => Set<AppointmentRow>();
    public DbSet<AuditRow> AuditEntries => Set<AuditRow>();
    public DbSet<IdempotencyRow> IdempotencyKeys => Set<IdempotencyRow>();
    public DbSet<PreflightRow> PreflightTokens => Set<PreflightRow>();
    public DbSet<PropertyRow> Properties => Set<PropertyRow>();
    public DbSet<ServiceRow> Services => Set<ServiceRow>();
    public DbSet<StaffRow> Staff => Set<StaffRow>();
    public DbSet<StaffQualificationRow> StaffQualifications => Set<StaffQualificationRow>();
    public DbSet<BufferPolicyRow> BufferPolicies => Set<BufferPolicyRow>();
    public DbSet<GuestRow> Guests => Set<GuestRow>();
    public DbSet<RoomRow> Rooms => Set<RoomRow>();

    protected override void OnModelCreating(ModelBuilder b)
    {

        b.Entity<AppointmentRow>(e =>
        {
            e.ToTable("appointment");
            e.HasKey(x => new { x.TenantId, x.PropertyId, x.AppointmentId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PropertyId).HasColumnName("property_id");
            e.Property(x => x.AppointmentId).HasColumnName("appointment_id");
            e.Property(x => x.GuestId).HasColumnName("guest_id");
            e.Property(x => x.ServiceId).HasColumnName("service_id");
            e.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.RoomId).HasColumnName("room_id");
            e.Property(x => x.StartUtc).HasColumnName("start_utc");
            // Maintained by a database trigger. Never written from here, or the
            // value the exclusion constraint indexes could disagree with the
            // start and duration it is supposed to be derived from.
            e.Property(x => x.EndUtc).HasColumnName("end_utc")
                .ValueGeneratedOnAddOrUpdate().Metadata
                .SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Ignore);
            e.Property(x => x.Status).HasColumnName("status");
            e.Property(x => x.RowVersion).HasColumnName("row_version");
            e.Property(x => x.ConfirmationNumber).HasColumnName("confirmation_number");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            e.Property(x => x.CreatedUtc).HasColumnName("created_utc");
            e.Property(x => x.UpdatedUtc).HasColumnName("updated_utc");
        });

        b.Entity<AuditRow>(e =>
        {
            e.ToTable("audit_entry");
            e.HasKey(x => x.AuditId);
            e.Property(x => x.AuditId).HasColumnName("audit_id").ValueGeneratedOnAdd();
            e.Property(x => x.AtUtc).HasColumnName("at_utc");
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PropertyId).HasColumnName("property_id");
            e.Property(x => x.Actor).HasColumnName("actor");
            e.Property(x => x.Action).HasColumnName("action");
            e.Property(x => x.Purpose).HasColumnName("purpose");
            e.Property(x => x.SubjectType).HasColumnName("subject_type");
            e.Property(x => x.SubjectId).HasColumnName("subject_id");
            e.Property(x => x.SubjectVersion).HasColumnName("subject_version");
            e.Property(x => x.BeforeHash).HasColumnName("before_hash");
            e.Property(x => x.AfterHash).HasColumnName("after_hash");
            e.Property(x => x.ConflictCodes).HasColumnName("conflict_codes");
            e.Property(x => x.SelectedResolution).HasColumnName("selected_resolution");
            e.Property(x => x.TargetStatus).HasColumnName("target_status");
            e.Property(x => x.Reason).HasColumnName("reason");
            e.Property(x => x.CorrelationId).HasColumnName("correlation_id");
        });

        b.Entity<IdempotencyRow>(e =>
        {
            e.ToTable("idempotency_key");
            e.HasKey(x => new { x.TenantId, x.PropertyId, x.Operation, x.Route, x.Key });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PropertyId).HasColumnName("property_id");
            e.Property(x => x.Operation).HasColumnName("operation");
            e.Property(x => x.Route).HasColumnName("route");
            e.Property(x => x.Key).HasColumnName("key");
            e.Property(x => x.RequestHash).HasColumnName("request_hash");
            e.Property(x => x.Completed).HasColumnName("completed");
            e.Property(x => x.StatusCode).HasColumnName("status_code");
            e.Property(x => x.ResponseJson).HasColumnName("response_json");
            e.Property(x => x.AtUtc).HasColumnName("at_utc");
        });

        b.Entity<PreflightRow>(e =>
        {
            e.ToTable("preflight_token");
            e.HasKey(x => new { x.TenantId, x.Token });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.Token).HasColumnName("token");
            e.Property(x => x.PropertyId).HasColumnName("property_id");
            e.Property(x => x.AppointmentId).HasColumnName("appointment_id");
            e.Property(x => x.ProposedStartUtc).HasColumnName("proposed_start_utc");
            e.Property(x => x.ProposedEndUtc).HasColumnName("proposed_end_utc");
            e.Property(x => x.ProposedProviderId).HasColumnName("proposed_provider_id");
            e.Property(x => x.ProposedRoomId).HasColumnName("proposed_room_id");
            e.Property(x => x.FromRowVersion).HasColumnName("from_row_version");
            e.Property(x => x.ConflictsJson).HasColumnName("conflicts_json").HasColumnType("jsonb");
            e.Property(x => x.IssuedUtc).HasColumnName("issued_utc");
            e.Property(x => x.ExpiresUtc).HasColumnName("expires_utc");
        });

        b.Entity<PropertyRow>(e =>
        {
            e.ToTable("property");
            e.HasKey(x => new { x.TenantId, x.PropertyId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PropertyId).HasColumnName("property_id");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.TimeZoneId).HasColumnName("time_zone_id");
            e.Property(x => x.OpenMinute).HasColumnName("open_minute");
            e.Property(x => x.CloseMinute).HasColumnName("close_minute");
        });

        b.Entity<ServiceRow>(e =>
        {
            e.ToTable("service");
            e.HasKey(x => new { x.TenantId, x.ServiceId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.ServiceId).HasColumnName("service_id");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.DurationMinutes).HasColumnName("duration_minutes");
        });

        b.Entity<StaffRow>(e =>
        {
            e.ToTable("staff");
            e.HasKey(x => new { x.TenantId, x.PropertyId, x.ProviderId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PropertyId).HasColumnName("property_id");
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
            e.Property(x => x.Assignable).HasColumnName("assignable");
        });

        b.Entity<StaffQualificationRow>(e =>
        {
            e.ToTable("staff_qualification");
            e.HasKey(x => new { x.TenantId, x.PropertyId, x.ProviderId, x.ServiceId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PropertyId).HasColumnName("property_id");
            e.Property(x => x.ProviderId).HasColumnName("provider_id");
            e.Property(x => x.ServiceId).HasColumnName("service_id");
            e.Property(x => x.GrantedUtc).HasColumnName("granted_utc");
            e.Property(x => x.ExpiresUtc).HasColumnName("expires_utc");
        });

        b.Entity<BufferPolicyRow>(e =>
        {
            e.ToTable("buffer_policy");
            // The table has no primary key — two partial unique indexes enforce
            // one property-wide row and one row per service instead, because a
            // nullable service_id cannot sit in a PK.
            e.HasNoKey();
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PropertyId).HasColumnName("property_id");
            e.Property(x => x.ServiceId).HasColumnName("service_id");
            e.Property(x => x.RoomTurnoverMinutes).HasColumnName("room_turnover_minutes");
            e.Property(x => x.ProviderTransitionMinutes).HasColumnName("provider_transition_minutes");
        });

        b.Entity<GuestRow>(e =>
        {
            e.ToTable("guest");
            e.HasKey(x => new { x.TenantId, x.GuestId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.GuestId).HasColumnName("guest_id");
            e.Property(x => x.DisplayAlias).HasColumnName("display_alias");
        });

        b.Entity<RoomRow>(e =>
        {
            e.ToTable("room");
            e.HasKey(x => new { x.TenantId, x.PropertyId, x.RoomId });
            e.Property(x => x.TenantId).HasColumnName("tenant_id");
            e.Property(x => x.PropertyId).HasColumnName("property_id");
            e.Property(x => x.RoomId).HasColumnName("room_id");
            e.Property(x => x.DisplayName).HasColumnName("display_name");
        });
    }
}
