using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CountTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "CountedQuantity",
                schema: "work",
                table: "WorkTasks",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_CountedQuantityBelongsToCounts",
                schema: "work",
                table: "WorkTasks",
                sql: "(\"Kind\" = 4\n OR \"CountedQuantity\" IS NULL)\nAND (\"Kind\" <> 4\n     OR \"Status\" <> 3\n     OR \"CountedQuantity\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_CountedQuantityBelongsToCounts",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "CountedQuantity",
                schema: "work",
                table: "WorkTasks");
        }
    }
}
