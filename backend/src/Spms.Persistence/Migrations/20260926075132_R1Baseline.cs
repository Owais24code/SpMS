using System;
using Microsoft.EntityFrameworkCore.Migrations;
using NpgsqlTypes;

#nullable disable

namespace Spms.Persistence.Migrations
{
    /// <summary>
    /// R1 baseline: the reviewed schema in database/schema/*.sql (generated from
    /// database/model/), applied verbatim and in order. EF generated the model
    /// snapshot for this migration from the same model, so the next
    /// `dotnet ef migrations add` diffs against the real schema instead of
    /// proposing to create it.
    ///
    /// Roles (000_roles.sql) are cluster-level and applied by the bootstrapper
    /// beforehand, not here.
    /// </summary>
    public partial class R1Baseline : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var (_, sql) in SchemaScripts.Baseline())
                migrationBuilder.Sql(sql);
        }

        /// <summary>
        /// There is no down migration for a baseline. Dropping every schema is
        /// not a rollback anyone should run by accident; restore from backup.
        /// </summary>
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("The R1 baseline cannot be reverted. Restore from backup instead.");
    }
}
