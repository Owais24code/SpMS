using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spms.Persistence.Migrations
{
    /// <summary>
    /// Batch 3, scheduling operations: the CON-006 undo columns on
    /// schedule_change_proposal, the room exclusion made deferrable for bulk
    /// moves, and core.active_properties() for the per-property job runner.
    /// SQL frozen in Sql/SchedulingOperations; the reference schema carries the
    /// same through database/model and database/tools/security_tail.sql.
    /// </summary>
    public partial class SchedulingOperations : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (_, sql) in SchemaScripts.ForMigration("SchedulingOperations"))
                migrationBuilder.Sql(sql);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Roll forward: restore from backup rather than dropping undo history.");
    }
}
