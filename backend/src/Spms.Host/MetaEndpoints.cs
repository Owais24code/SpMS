using Spms.SharedKernel;
using Spms.Web;

namespace Spms.Host;

/// <summary>Health probes. Unauthenticated, outside the request transaction.</summary>
public static class MetaEndpoints
{
    public static void MapMeta(this IEndpointRouteBuilder app, IHostEnvironment environment)
    {
        // Liveness has no token and touches nothing: listening is all it claims.
        app.MapGet("/health/live", (IClock clock) => Results.Json(new
        {
            status = "ok",
            utc = clock.UtcNow.ToString("O"),
        }, Json.Options));

        // Readiness probes the database with a trivial scoped round trip.
        app.MapGet("/health/ready", Ready);
        app.MapGet("/health", Ready);

        // Development only: the 500 path is the one no client can reach on
        // purpose, so without this it would ship on inspection alone.
        if (environment.IsDevelopment())
        {
            app.MapGet("/dev/throw", IResult () =>
                throw new InvalidOperationException("Deliberate failure exercising the error handler."));

            // Runs the owner-side maintenance pass now (partitions ahead, old outbox months).
            app.MapPost("/dev/maintenance/run", async (IConfiguration config, Workers.MaintenanceOptions options, ILoggerFactory loggers, CancellationToken ct) =>
                Results.Json(await Workers.DatabaseMaintenance.RunAsync(config.GetConnectionString("SpmsOwner") ?? config.GetConnectionString("Spms") ?? "",
                    config["Database:MigrationRole"] ?? "spms_owner", options, loggers.CreateLogger("Maintenance"), ct), Json.Options));

            // Runs every housekeeping job now, at every property, instead of waiting for its interval.
            app.MapPost("/dev/jobs/run", async (Workers.JobRunner runner, CancellationToken ct) =>
                Results.Json(new { changed = await runner.RunDueAsync(force: true, ct) }, Json.Options));
        }
    }

    private static async Task<IResult> Ready(IClock clock, Npgsql.NpgsqlDataSource db, CancellationToken ct)
    {
        try
        {
            await using var cmd = db.CreateCommand("SELECT 1");
            await cmd.ExecuteScalarAsync(ct);
            return Results.Json(new { status = "ok", utc = clock.UtcNow.ToString("O"), release = "R1", persistence = "postgresql" }, Json.Options);
        }
        catch (Npgsql.NpgsqlException)
        {
            return Results.Json(new { status = "unavailable", utc = clock.UtcNow.ToString("O") }, Json.Options, statusCode: 503);
        }
    }
}
