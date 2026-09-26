using System.Text.Json;
using Npgsql;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Host.Authorization;

/// <summary>
/// Keeps OpenFGA's stored tuples in step with the SpMS tables they mirror.
/// The tables are the source of truth; this handler and the reconciler are the
/// only writers of stored tuples.
///
///   role assignment  user:&lt;principal&gt; &lt;role&gt; property:&lt;p&gt; | tenant:&lt;t&gt;
///   property         tenant:&lt;t&gt; tenant property:&lt;p&gt;
///   guest ownership  user:&lt;principal&gt; owner guest:&lt;g&gt;   (+ tenant:&lt;t&gt; tenant guest:&lt;g&gt;)
///   delegation       user:&lt;delegate&gt; delegate_&lt;action&gt; guest:&lt;g&gt; with active_delegation
/// </summary>
public sealed class FgaTupleSyncHandler(FgaTupleWriter writer, FgaClientHolder fga, NpgsqlDataSource dataSource, PersistenceOptions persistence)
    : IOutboxHandler
{
    private static readonly HashSet<string> Types =
    [
        EventTypes.RoleAssignmentChanged, EventTypes.GuestOwnershipChanged, EventTypes.PropertyRegistered,
        EventTypes.DelegationChanged, EventTypes.DeviceRegistrationChanged,
    ];

    public string Name => "fga-tuple-sync";
    public bool Handles(string eventType) => fga.Ready && Types.Contains(eventType);

    public async Task HandleAsync(OutboxMessage m, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(m.PayloadJson);
        var p = doc.RootElement;
        switch (m.EventType)
        {
            case EventTypes.RoleAssignmentChanged:
            {
                var principal = p.GetProperty("principalId").GetGuid();
                var role = p.GetProperty("roleCode").GetString()!;
                var property = p.TryGetProperty("propertyId", out var pp) && pp.ValueKind == JsonValueKind.String ? pp.GetGuid() : (Guid?)null;
                var tuple = new FgaTuple(Fga.User(principal), role, property is { } pid ? Fga.Property(pid) : Fga.Tenant(m.TenantId));
                if (p.GetProperty("status").GetString() == "Active") await writer.WriteAsync([tuple], ct);
                else await writer.DeleteAsync([tuple], ct);

                await MarkSyncedAsync(m.TenantId, property,
                    "UPDATE workforce.staff_role_assignment SET fga_synced_at = now(), version = version + 1 WHERE staff_role_assignment_id = @id",
                    p.GetProperty("assignmentId").GetGuid(), ct);
                break;
            }
            case EventTypes.PropertyRegistered:
                await writer.WriteAsync([new FgaTuple(Fga.Tenant(m.TenantId), "tenant", Fga.Property(m.AggregateId))], ct);
                break;
            case EventTypes.GuestOwnershipChanged:
            {
                var guest = m.AggregateId;
                var principal = p.GetProperty("principalId").GetGuid();
                await writer.WriteAsync([
                    new FgaTuple(Fga.User(principal), "owner", Fga.Guest(guest)),
                    new FgaTuple(Fga.Tenant(m.TenantId), "tenant", Fga.Guest(guest)),
                ], ct);
                break;
            }
            case EventTypes.DelegationChanged:
            {
                var guest = p.GetProperty("guestId").GetGuid();
                var delegatePrincipal = p.GetProperty("delegatePrincipalId").GetGuid();
                var actions = p.GetProperty("actions").EnumerateArray().Select(a => a.GetString()!).ToList();
                var tuples = actions.Select(a => new FgaTuple(Fga.User(delegatePrincipal), DelegateRelation(a), Fga.Guest(guest))).ToList();
                if (p.GetProperty("status").GetString() == "Active")
                {
                    var context = new
                    {
                        grant_expires_at = p.GetProperty("expiresAt").GetString(),
                        allowed_property_ids = p.TryGetProperty("propertyIds", out var ids) && ids.ValueKind == JsonValueKind.Array
                            ? ids.EnumerateArray().Select(x => x.GetString()).ToArray() : [],
                    };
                    foreach (var t in tuples) await writer.WriteConditionalAsync(t, "active_delegation", context, ct);
                }
                else
                {
                    await writer.DeleteAsync(tuples, ct);
                }
                await MarkSyncedAsync(m.TenantId, null,
                    "UPDATE guest.delegated_authority SET fga_synced_at = now(), version = version + 1 WHERE delegated_authority_id = @id",
                    m.AggregateId, ct);
                break;
            }
            case EventTypes.DeviceRegistrationChanged:
            {
                var property = m.PropertyId ?? p.GetProperty("propertyId").GetGuid();
                var tuples = new List<FgaTuple>
                {
                    new(Fga.Property(property), "property", Fga.Device(m.AggregateId)),
                    new(Fga.Device(m.AggregateId), "registered_device", Fga.Property(property)),
                };
                if (p.GetProperty("status").GetString() == "Active") await writer.WriteAsync(tuples, ct);
                else await writer.DeleteAsync(tuples, ct);
                break;
            }
        }
    }

    /// <summary>
    /// IDN-003's action names to the model's relations. "ViewItinerary" is
    /// delegate_view; there is no relation that reaches intake content.
    /// </summary>
    public static string DelegateRelation(string action) => action switch
    {
        "Book" => "delegate_book",
        "Cancel" => "delegate_cancel",
        "Reschedule" => "delegate_reschedule",
        "Pay" => "delegate_pay",
        "ViewItinerary" => "delegate_view",
        "CompleteIntake" => "delegate_intake",
        _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Not a delegation action."),
    };

    private async Task MarkSyncedAsync(Guid tenant, Guid? property, string sql, Guid id, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var scope = new ExecutionScope();
        scope.Set(tenant, property is { } p ? [p] : [], property, null, ActorType.System, "fga-sync");
        await ScopeSql.ApplyAsync(conn, tx, scope, persistence.RuntimeRole, ct);
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("id", id);
        await cmd.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }
}

/// <summary>
/// Full reconciliation: rewrites every stored tuple from the tables. Run at
/// development start-up, by `Spms.Host --fga-sync`, and after restoring an
/// OpenFGA store. Idempotent (duplicate writes are ignored).
/// </summary>
public sealed class FgaReconciler(FgaTupleWriter writer, NpgsqlDataSource dataSource, PersistenceOptions persistence, Workers.OutboxOptions outbox,
    ILogger<FgaReconciler> logger)
{
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var tenants = new List<Guid>();
        await using (var conn = await dataSource.OpenConnectionAsync(ct))
        await using (var tx = await conn.BeginTransactionAsync(ct))
        {
            await using (var role = new NpgsqlCommand($"SET LOCAL ROLE \"{outbox.Role}\"", conn, tx)) await role.ExecuteNonQueryAsync(ct);
            await using var cmd = new NpgsqlCommand("SELECT t FROM core.active_tenants() t", conn, tx);
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct)) tenants.Add(r.GetGuid(0));
        }

        var total = 0;
        foreach (var tenant in tenants)
        {
            var tuples = await ReadTenantAsync(tenant, ct);
            foreach (var chunk in tuples.Chunk(50)) await writer.WriteAsync(chunk, ct);
            total += tuples.Count;
        }
        logger.LogInformation("OpenFGA reconciliation wrote {Count} tuples for {Tenants} tenant(s)", total, tenants.Count);
        return total;
    }

    private async Task<List<FgaTuple>> ReadTenantAsync(Guid tenant, CancellationToken ct)
    {
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var scope = new ExecutionScope();
        scope.Set(tenant, [], null, null, ActorType.System, "fga-reconcile");
        await ScopeSql.ApplyAsync(conn, tx, scope, persistence.RuntimeRole, ct);

        var properties = new List<Guid>();
        await using (var cmd = new NpgsqlCommand("SELECT property_id FROM core.property", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
            while (await r.ReadAsync(ct)) properties.Add(r.GetGuid(0));

        scope.Set(tenant, properties, null, null, ActorType.System, "fga-reconcile");
        await ScopeSql.ApplyAsync(conn, tx, scope, null, ct);

        var tuples = properties.Select(p => new FgaTuple(Fga.Tenant(tenant), "tenant", Fga.Property(p))).ToList();

        await using (var cmd = new NpgsqlCommand("""
            SELECT s.principal_id, a.role_code, a.property_id
              FROM workforce.staff_role_assignment a
              JOIN workforce.staff s ON s.staff_id = a.staff_id
             WHERE a.status = 'Active' AND s.principal_id IS NOT NULL
               AND a.effective_from <= now() AND (a.effective_to IS NULL OR a.effective_to > now())
            """, conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
                tuples.Add(new FgaTuple(Fga.User(r.GetGuid(0)), r.GetString(1),
                    r.IsDBNull(2) ? Fga.Tenant(tenant) : Fga.Property(r.GetGuid(2))));
        }

        await using (var cmd = new NpgsqlCommand(
            "SELECT guest_id, principal_id FROM guest.guest WHERE principal_id IS NOT NULL AND status IN ('Active', 'Restricted')", conn, tx))
        await using (var r = await cmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                tuples.Add(new FgaTuple(Fga.User(r.GetGuid(1)), "owner", Fga.Guest(r.GetGuid(0))));
                tuples.Add(new FgaTuple(Fga.Tenant(tenant), "tenant", Fga.Guest(r.GetGuid(0))));
            }
        }
        await tx.CommitAsync(ct);
        return tuples;
    }
}
