using Microsoft.EntityFrameworkCore.Design;
using Spms.Persistence;

namespace Spms.Host;

/// <summary>
/// For dotnet-ef (migrations add / script). Uses every module's contributor, so
/// the snapshot is the whole model:
///   dotnet ef migrations add Name --project src/Spms.Persistence --startup-project src/Spms.Host
/// </summary>
public sealed class DesignTimeContextFactory : IDesignTimeDbContextFactory<SpmsDbContext>
{
    public SpmsDbContext CreateDbContext(string[] args) =>
        DatabaseBootstrapper.CreateMigrationContext(
            Environment.GetEnvironmentVariable("SPMS_CONNECTION") ?? "Host=localhost;Database=spms_design;Username=postgres",
            SpmsModules.Contributors(), migrationRole: null);
}
