using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReceiptAgeProjection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FirstReceivedAt",
                schema: "inventory",
                table: "StockBalances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReceiptAgeProjectionState",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    Completed = table.Column<bool>(type: "boolean", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReceiptAgeProjectionState", x => x.Id);
                });

            migrationBuilder.InsertData(
                schema: "inventory",
                table: "ReceiptAgeProjectionState",
                columns: new[] { "Id", "Completed", "CompletedAt" },
                values: new object[] { 1, false, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReceiptAgeProjectionState",
                schema: "inventory");

            migrationBuilder.DropColumn(
                name: "FirstReceivedAt",
                schema: "inventory",
                table: "StockBalances");
        }
    }
}
