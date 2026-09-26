using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spms.Persistence.Migrations
{
    /// <summary>
    /// Batch 4, guests and intake: scheduling.intake_status(), the desk's
    /// status-only view of intake for the arrivals list (the API role cannot
    /// read intake tables). SQL frozen in Sql/GuestsAndIntake; the reference
    /// schema carries the same through database/tools/security_tail.sql.
    /// The spms_erasure membership for spms_app is cluster-level (000_roles.sql).
    /// </summary>
    public partial class GuestsAndIntake : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (_, sql) in SchemaScripts.ForMigration("GuestsAndIntake"))
                migrationBuilder.Sql(sql);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("DROP FUNCTION scheduling.intake_status(uuid[]);");
    }
}
