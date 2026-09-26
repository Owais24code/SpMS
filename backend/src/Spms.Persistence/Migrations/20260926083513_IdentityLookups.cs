using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spms.Persistence.Migrations
{
    /// <summary>
    /// core.resolve_property: tenant and property from their public codes, for
    /// callers that arrive with no identity (guest web, kiosk). SQL frozen in
    /// Sql/IdentityLookups; the reference schema carries the same function in
    /// database/tools/security_tail.sql.
    /// </summary>
    public partial class IdentityLookups : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (_, sql) in SchemaScripts.ForMigration("IdentityLookups"))
                migrationBuilder.Sql(sql);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP FUNCTION core.resolve_property(text, text);");
    }
}
