using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CountsAndReconciliationRuns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ReconciliationRuns",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Trigger = table.Column<int>(type: "integer", nullable: false),
                    MovementsReplayed = table.Column<long>(type: "bigint", nullable: false),
                    HoldingsChecked = table.Column<int>(type: "integer", nullable: false),
                    LedgerSum = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DriftCount = table.Column<int>(type: "integer", nullable: false),
                    WorstDrift = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ElapsedMilliseconds = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReconciliationRuns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockCounts",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LicensePlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExpectedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CountedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Variance = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AtMovementSequence = table.Column<long>(type: "bigint", nullable: false),
                    AdjustmentMovementId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CountedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CountedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CountedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockCounts", x => x.Id);
                    table.CheckConstraint("CK_StockCounts_NonNegativeCounted", "\"CountedQuantity\" >= 0");
                    table.CheckConstraint("CK_StockCounts_VarianceHasMovement", "(\"Variance\" = 0) = (\"AdjustmentMovementId\" IS NULL)");
                    table.CheckConstraint("CK_StockCounts_VarianceIsDifference", "\"Variance\" = \"CountedQuantity\" - \"ExpectedQuantity\"");
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReconciliationRuns_RunAt",
                schema: "inventory",
                table: "ReconciliationRuns",
                column: "RunAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_AdjustmentMovementId",
                schema: "inventory",
                table: "StockCounts",
                column: "AdjustmentMovementId");

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_IdempotencyKey",
                schema: "inventory",
                table: "StockCounts",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_ItemId_LotId_LocationId_LicensePlateId_Status_C~",
                schema: "inventory",
                table: "StockCounts",
                columns: new[] { "ItemId", "LotId", "LocationId", "LicensePlateId", "Status", "CountedAt" })
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_WarehouseId_CountedAt",
                schema: "inventory",
                table: "StockCounts",
                columns: new[] { "WarehouseId", "CountedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StockCounts_WorkTaskId",
                schema: "inventory",
                table: "StockCounts",
                column: "WorkTaskId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReconciliationRuns",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockCounts",
                schema: "inventory");
        }
    }
}
