using Npgsql;
using Spms.Persistence;
using Spms.SharedKernel;

namespace Spms.Host;

public static class SchemaGate
{
    public static async Task VerifyAsync(IServiceProvider services, string ownerConnection, ILogger logger)
    {
        var problems = new List<string>();

        await using (var migrations = DatabaseBootstrapper.CreateMigrationContext(ownerConnection, SpmsModules.Contributors(), null))
        {
            var pending = (await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions
                .GetPendingMigrationsAsync(migrations.Database)).ToList();
            problems.AddRange(pending.Select(p => $"migration {p} has not been applied"));

            if (pending.Count == 0)
            {
                await using var conn = new NpgsqlConnection(ownerConnection);
                await conn.OpenAsync();
                problems.AddRange(await SchemaDrift.CompareAsync(migrations, conn));

                // The one invariant the application cannot enforce itself (CON-002
                // across instances) must exist, or the process refuses to start.
                await using var cmd = new NpgsqlCommand(
                    "SELECT 1 FROM pg_constraint WHERE conname = 'appointment_room_no_overlap'", conn);
                if (await cmd.ExecuteScalarAsync() is null)
                    problems.Add("constraint scheduling.appointment_room_no_overlap is missing");
            }
        }

        if (problems.Count > 0)
        {
            foreach (var p in problems) logger.LogCritical("Schema check failed: {Problem}", p);
            throw new InvalidOperationException(
                $"The database does not match this build ({problems.Count} problem(s)): {string.Join("; ", problems.Take(10))}");
        }
    }
}

public static class ProtectionSetup
{
    /// <summary>
    /// Field protection keys. Deployed: Protection:Keys:{version} and
    /// Protection:LookupKey come from Key Vault references (base64, 32 bytes),
    /// or Protection:KeyVault:KeyId selects the Key Vault-wrapped ring. There is
    /// no generated fallback outside Development: a process that invents a key
    /// writes ciphertext nobody can read after the next restart.
    /// </summary>
    public static IServiceCollection AddSpmsProtection(this IServiceCollection services, IConfiguration config, IHostEnvironment env)
    {
        var section = config.GetSection("Protection");
        var keys = section.GetSection("Keys").GetChildren().ToDictionary(c => c.Key, c => c.Value ?? "");
        var active = section["ActiveKey"];
        var lookup = section["LookupKey"];

        if (keys.Count == 0 || string.IsNullOrWhiteSpace(active) || string.IsNullOrWhiteSpace(lookup))
        {
            if (!env.IsDevelopment())
                throw new InvalidOperationException("Protection keys are not configured (Protection:ActiveKey, Protection:Keys, Protection:LookupKey).");
            // Development only: fixed, published, worthless keys, stable across restarts.
            keys = new Dictionary<string, string> { ["dev1"] = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(i + 1)).ToArray()) };
            active = "dev1";
            lookup = Convert.ToBase64String(new byte[32].Select((_, i) => (byte)(64 - i)).ToArray());
        }

        services.AddSingleton<IKeyRing>(new StaticKeyRing(keys, active, lookup));
        services.AddSingleton<IFieldProtector, AesGcmFieldProtector>();
        return services;
    }
}

public static class DevSeed
{
    public static async Task ApplyAsync(string ownerConnection, string role, CancellationToken ct = default)
    {
        using var stream = typeof(DevSeed).Assembly.GetManifestResourceStream("Spms.Seed.dev.sql")!;
        using var reader = new StreamReader(stream);
        var sql = await reader.ReadToEndAsync(ct);

        await using var conn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(ownerConnection) { Pooling = false }.ConnectionString);
        await conn.OpenAsync(ct);
        await using (var setRole = new NpgsqlCommand($"SET ROLE \"{role.Replace("\"", "\"\"")}\"", conn))
            await setRole.ExecuteNonQueryAsync(ct);
        await using var cmd = new NpgsqlCommand(sql, conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
