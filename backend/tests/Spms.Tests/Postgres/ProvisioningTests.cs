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
    public async Task A_local_admin_gets_an_email_and_password_sign_in_that_must_be_changed()
    {
        var code = $"prov-{Guid.NewGuid():N}"[..20];
        var email = $"Admin-{Guid.NewGuid():N}@Example.test";
        var spec = new ProvisionOptions
        {
            Tenant = { Code = code, Name = "Local spa group" },
            Properties = [new() { Code = "main", Name = "Main spa", Timezone = "Asia/Kolkata" }],
            Admins = [new() { Name = "Local admin", Email = email, Password = "First-password-1" }],
        };
        Assert.Equal(3, (await Provisioning.RunAsync(fixture.ConnectionString, "spms_owner", spec, Issuer, NullLogger.Instance)).Count);

        var id = await Resolver().ResolveAsync(LocalSessionIssuer.Issuer, Passwords.Subject(email));
        Assert.NotNull(id);
        Assert.Contains("platform_admin", id.RoleCodes);
        var hash = await fixture.ScalarAsync<string>(
            $"SELECT password_hash || '|' || must_change_password FROM core.principal_login WHERE idp_subject = '{Passwords.Subject(email)}'");
        Assert.True(Passwords.Verify("First-password-1", hash.Split('|')[0]));
        Assert.Equal("True", hash.Split('|')[1], ignoreCase: true);
        Assert.Empty(await Provisioning.RunAsync(fixture.ConnectionString, "spms_owner", spec, Issuer, NullLogger.Instance));
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
