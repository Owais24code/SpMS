using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Spms.Host.Identity;
using Spms.Host.Operations;
using Spms.Persistence;
using Spms.Tests.Support;
using Xunit;

namespace Spms.Tests.Postgres;

public class ProvisioningTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Issuer = "https://login.microsoftonline.com/provision-test/v2.0";

    private PrincipalResolver Resolver() => new(
        fixture.Services.GetRequiredService<Npgsql.NpgsqlDataSource>(), fixture.Services.GetRequiredService<PersistenceOptions>(),
        new MemoryCache(new MemoryCacheOptions()));

    private static ProvisionOptions Spec(string code, string oid) => new()
    {
        Tenant = { Code = code, Name = "Provisioned spa group", Currency = "INR" },
        Properties = [new() { Code = "main", Name = "Main spa", Timezone = "Asia/Kolkata" }, new() { Code = "second", Name = "Second spa", Timezone = "Asia/Kolkata" }],
        Admins = [new() { ObjectId = oid, Name = "First admin", Email = "admin@example.test" }],
    };

    [RequiresPostgres]
    public async Task A_new_deployment_gets_its_tenant_properties_and_first_admin_and_a_second_run_changes_nothing()
    {
        var code = $"prov-{Guid.NewGuid():N}"[..20];
        var oid = Guid.NewGuid().ToString();

        var first = await Provisioning.RunAsync(fixture.ConnectionString, "spms_owner", Spec(code, oid), Issuer, NullLogger.Instance);
        Assert.Equal(4, first.Count); // tenant, two properties, one admin

        var id = await Resolver().ResolveAsync(Issuer, oid);
        Assert.NotNull(id);
        Assert.Equal(Provisioning.Id("tenant", code), id.TenantId);
        Assert.Equal(["configuration_approver", "platform_admin", "spa_manager"], id.RoleCodes.Order());
        Assert.Equal(2, id.Properties.Count); // tenant-wide roles reach every property
        Assert.NotNull(id.StaffId);

        var second = await Provisioning.RunAsync(fixture.ConnectionString, "spms_owner", Spec(code, oid), Issuer, NullLogger.Instance);
        Assert.Empty(second);
    }

    [RequiresPostgres]
    public async Task An_incomplete_specification_is_refused_before_anything_is_written()
    {
        var spec = Spec("prov-bad", "not-a-guid");
        spec.Properties[0].Timezone = "Mars/Olympus";
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Provisioning.RunAsync(fixture.ConnectionString, "spms_owner", spec, Issuer, NullLogger.Instance));
        Assert.Contains("IANA timezone", e.Message);
        Assert.Contains("object id", e.Message);
    }

    [Fact]
    public void Ids_are_stable_per_natural_key()
    {
        Assert.Equal(Provisioning.Id("tenant", "aarfid"), Provisioning.Id("tenant", "aarfid"));
        Assert.NotEqual(Provisioning.Id("tenant", "aarfid"), Provisioning.Id("property", "aarfid"));
        Assert.Equal('8', Provisioning.Id("tenant", "aarfid").ToString()[14]);
    }
}
