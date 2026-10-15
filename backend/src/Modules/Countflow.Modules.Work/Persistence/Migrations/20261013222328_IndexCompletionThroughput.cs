using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class IndexCompletionThroughput : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_WarehouseId_CompletedAt",
                schema: "work",
                table: "WorkTasks",
                columns: new[] { "WarehouseId", "CompletedAt" },
                filter: "\"Status\" = 3")
                .Annotation("Npgsql:IndexInclude", new[] { "Kind" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_WarehouseId_CompletedAt",
                schema: "work",
                table: "WorkTasks");
        }
    }
}
