using Npgsql;

namespace Spms.Api.Infrastructure;

/// <summary>
/// Development reference data and a day of demo appointments.
///
/// Plain SQL against the real schema, and idempotent: every statement is an
/// upsert or guarded, so restarting the app does not fail on a second run and
/// does not quietly duplicate. It seeds the same reference data the tests
/// expect, which is why the acceptance sweep can talk to a freshly started
/// instance without any fixture step of its own.
///
/// Appointments are inserted with explicit statuses rather than by walking the
/// state machine: the seed is data, not a workflow, and every row lands at
/// row_version 1 so a seeded appointment's ETag does not depend on how many
/// transitions its status happened to need.
/// </summary>
public static class DevSeed
{
    private const string Tenant = "tenant-demo";
    private const string Property = "prop-riverside";

    public static async Task ApplyAsync(IServiceProvider services, CancellationToken ct = default)
    {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        await using var conn = await dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);

        await Exec(conn, tx, ct, """
            INSERT INTO tenant (tenant_id, display_name) VALUES (@t, 'AARFID Demo')
            ON CONFLICT (tenant_id) DO NOTHING;

            INSERT INTO property (tenant_id, property_id, display_name, time_zone_id, open_minute, close_minute)
            VALUES (@t, @p, 'Riverside Spa', 'America/New_York', 540, 1020)
            ON CONFLICT (tenant_id, property_id) DO UPDATE
              SET time_zone_id = EXCLUDED.time_zone_id,
                  open_minute  = EXCLUDED.open_minute,
                  close_minute = EXCLUDED.close_minute;

            INSERT INTO service (tenant_id, service_id, display_name, duration_minutes) VALUES
              (@t, 'svc-deep',     'Deep tissue 90',  90),
              (@t, 'svc-aroma',    'Aromatherapy 60', 60),
              (@t, 'svc-facial',   'Facial 45',       45),
              (@t, 'svc-hotstone', 'Hot stone 60',    60),
              (@t, 'svc-swedish',  'Swedish 60',      60),
              (@t, 'svc-peel',     'Peel 30',         30)
            ON CONFLICT (tenant_id, service_id) DO UPDATE
              SET display_name = EXCLUDED.display_name, duration_minutes = EXCLUDED.duration_minutes;

            INSERT INTO room (tenant_id, property_id, room_id, display_name) VALUES
              (@t, @p, 'room-1', 'Treatment 1'),
              (@t, @p, 'room-2', 'Treatment 2'),
              (@t, @p, 'room-4', 'Treatment 4'),
              (@t, @p, 'room-5', 'Treatment 5'),
              (@t, @p, 'room-suite1', 'Suite 1'),
              (@t, @p, 'room-suite3', 'Suite 3')
            ON CONFLICT (tenant_id, property_id, room_id) DO NOTHING;

            INSERT INTO staff (tenant_id, property_id, provider_id, display_name, assignable) VALUES
              (@t, @p, 'prov-lena',  'Lena',  true),
              (@t, @p, 'prov-marco', 'Marco', true),
              (@t, @p, 'prov-priya', 'Priya', true)
            ON CONFLICT (tenant_id, property_id, provider_id) DO NOTHING;

            -- Qualifications are the CON-003 source of truth. prov-priya is
            -- deliberately NOT qualified for the massage services, so the
            -- unqualified-provider refusal is demonstrable on the demo board.
            -- Backdated: granted_utc defaults to now(), which would make a
            -- qualification start mid-morning and refuse every appointment
            -- earlier in the same day.
            INSERT INTO staff_qualification (tenant_id, property_id, provider_id, service_id, granted_utc) VALUES
              (@t, @p, 'prov-lena', 'svc-deep', 'epoch'),
              (@t, @p, 'prov-lena', 'svc-aroma', 'epoch'),
              (@t, @p, 'prov-lena', 'svc-hotstone', 'epoch'),
              (@t, @p, 'prov-lena', 'svc-swedish', 'epoch'),
              (@t, @p, 'prov-marco', 'svc-deep', 'epoch'),
              (@t, @p, 'prov-marco', 'svc-swedish', 'epoch'),
              (@t, @p, 'prov-marco', 'svc-hotstone', 'epoch'),
              (@t, @p, 'prov-priya', 'svc-facial', 'epoch'),
              (@t, @p, 'prov-priya', 'svc-peel', 'epoch')
            ON CONFLICT (tenant_id, property_id, provider_id, service_id) DO NOTHING;

            INSERT INTO guest (tenant_id, guest_id, display_alias) VALUES
              (@t, 'guest-4821', 'Guest 4821'),
              (@t, 'guest-4822', 'Guest 4822'),
              (@t, 'guest-4823', 'Guest 4823'),
              (@t, 'guest-4824', 'Guest 4824'),
              (@t, 'guest-4825', 'Guest 4825')
            ON CONFLICT (tenant_id, guest_id) DO NOTHING;
            """);

        // Buffer policy: the spec baselines, now configuration rather than a
        // compiled constant. Upserted through the partial unique index, which
        // is why this is not an ON CONFLICT on a primary key.
        await Exec(conn, tx, ct, """
            DELETE FROM buffer_policy WHERE tenant_id = @t AND property_id = @p AND service_id IS NULL;
            INSERT INTO buffer_policy
                (tenant_id, property_id, service_id, room_turnover_minutes, provider_transition_minutes)
            VALUES (@t, @p, NULL, 15, 10);
            """);

        // A day of demo appointments, conflict-free against each other so the
        // board opens clean and a demo conflict is something the operator
        // creates rather than something they have to clear first.
        await Exec(conn, tx, ct, """
            INSERT INTO appointment
                (tenant_id, property_id, appointment_id, guest_id, service_id, duration_minutes,
                 provider_id, room_id, start_utc, end_utc, status, row_version,
                 confirmation_number, correlation_id, created_utc, updated_utc)
            VALUES
              (@t, @p, 'appt-seed00001', 'guest-4821', 'svc-deep',     90, 'prov-lena',  'room-suite3',
               (CURRENT_DATE + time '13:00') AT TIME ZONE 'UTC', 'epoch', 'InService',  1, 'AAR000000001', 'seed', now(), now()),
              (@t, @p, 'appt-seed00002', 'guest-4822', 'svc-facial',   45, 'prov-priya', 'room-2',
               (CURRENT_DATE + time '13:30') AT TIME ZONE 'UTC', 'epoch', 'Confirmed',  1, 'AAR000000002', 'seed', now(), now()),
              (@t, @p, 'appt-seed00003', 'guest-4823', 'svc-aroma',    60, NULL,         'room-suite1',
               (CURRENT_DATE + time '15:00') AT TIME ZONE 'UTC', 'epoch', 'Confirmed',  1, 'AAR000000003', 'seed', now(), now()),
              (@t, @p, 'appt-seed00004', 'guest-4824', 'svc-hotstone', 60, 'prov-marco', 'room-4',
               (CURRENT_DATE + time '14:15') AT TIME ZONE 'UTC', 'epoch', 'Confirmed',  1, 'AAR000000004', 'seed', now(), now()),
              (@t, @p, 'appt-seed00005', 'guest-4825', 'svc-swedish',  60, 'prov-marco', 'room-5',
               (CURRENT_DATE + time '16:00') AT TIME ZONE 'UTC', 'epoch', 'Draft',      1, 'AAR000000005', 'seed', now(), now())
            ON CONFLICT (tenant_id, property_id, appointment_id) DO NOTHING;
            """);

        await tx.CommitAsync(ct);
    }

    private static async Task Exec(NpgsqlConnection conn, NpgsqlTransaction tx, CancellationToken ct, string sql)
    {
        await using var cmd = new NpgsqlCommand(sql, conn, tx);
        cmd.Parameters.AddWithValue("t", Tenant);
        cmd.Parameters.AddWithValue("p", Property);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
