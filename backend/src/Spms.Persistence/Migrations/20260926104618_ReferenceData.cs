using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Spms.Persistence.Migrations
{
    /// <summary>
    /// Batch 6, reference data: a stock count's generated variance is NULL
    /// until the count is recorded (it was NOT NULL, so no count could be
    /// opened). SQL frozen in Sql/ReferenceData.
    /// </summary>
    public partial class ReferenceData : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (_, sql) in SchemaScripts.ForMigration("ReferenceData"))
                migrationBuilder.Sql(sql);
        }

        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql("ALTER TABLE inventory.stock_count ALTER COLUMN variance_quantity SET NOT NULL;");
    }
}
