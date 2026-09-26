using Microsoft.EntityFrameworkCore;
using Spms.Persistence;

namespace Spms.Modules.Intake.Application;

/// <summary>
/// SEC-008: intake tables are readable only by spms_intake, which the API role
/// may SET but does not inherit. Entering switches the transaction's role for
/// the intake statements; leaving switches back, so audit and outbox rows are
/// written as spms_app as usual. Nothing but intake rows may be pending when
/// the role changes, or they would be flushed under the wrong role.
/// </summary>
public sealed class IntakeRole(SpmsDbContext db, PersistenceOptions options)
{
    public async Task<Lease> EnterAsync(CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Intake work runs inside the request's transaction.");
        await db.SaveChangesAsync(ct);   // flush anything staged as spms_app first
        await db.Database.ExecuteSqlRawAsync("SET LOCAL ROLE spms_intake", ct);
        return new Lease(db, string.IsNullOrWhiteSpace(options.RuntimeRole) ? "spms_app" : options.RuntimeRole);
    }

    public sealed class Lease(SpmsDbContext db, string runtimeRole) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // A failed statement aborts the transaction; the role goes with it on rollback.
            try
            {
                var sql = "SET LOCAL ROLE \"" + runtimeRole.Replace("\"", "\"\"") + "\"";
                await db.Database.ExecuteSqlRawAsync(sql);
            }
            catch (Npgsql.PostgresException e) when (e.SqlState == "25P02")
            {
            }
        }
    }
}
