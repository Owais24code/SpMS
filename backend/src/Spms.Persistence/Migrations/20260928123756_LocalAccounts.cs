using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spms.Persistence.Migrations
{
    /// <summary>
    /// Local email + password accounts (no Entra): principal_login gains the
    /// Local login type, a password hash, the must-change flag and lockout
    /// counters. SQL frozen in Sql/LocalAccounts.
    /// </summary>
    public partial class LocalAccounts : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (_, sql) in SchemaScripts.ForMigration("LocalAccounts"))
                migrationBuilder.Sql(sql);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("""
                ALTER TABLE core.principal_login DROP CONSTRAINT principal_login_local_has_password;
                ALTER TABLE core.principal_login DROP CONSTRAINT principal_login_failed_attempts_ck;
                ALTER TABLE core.principal_login DROP CONSTRAINT principal_login_login_type_ck;
                ALTER TABLE core.principal_login ADD CONSTRAINT principal_login_login_type_ck CHECK (login_type IN ('EntraUser', 'EntraApplication'));
                ALTER TABLE core.principal_login DROP COLUMN password_hash, DROP COLUMN must_change_password,
                    DROP COLUMN failed_attempts, DROP COLUMN locked_until;
                """);
    }
}
