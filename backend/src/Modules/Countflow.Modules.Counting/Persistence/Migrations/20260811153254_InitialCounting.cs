using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Counting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialCounting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "counting");

            migrationBuilder.CreateTable(
                name: "CountPlans",
                schema: "counting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    RequestedTasks = table.Column<int>(type: "integer", nullable: false),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CancelledReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CountPlans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CountPlanLines",
                schema: "counting",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    CountPlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    BalanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LicensePlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Condition = table.Column<int>(type: "integer", nullable: false),
                    ExpectedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    Score = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    WorkTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CountPlanLines", x => x.Id);
                    table.CheckConstraint("CK_CountPlanLines_NonNegativeExpected", "\"ExpectedQuantity\" >= 0");
                    table.ForeignKey(
                        name: "FK_CountPlanLines_CountPlans_CountPlanId",
                        column: x => x.CountPlanId,
                        principalSchema: "counting",
                        principalTable: "CountPlans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CountPlanLines_BalanceId",
                schema: "counting",
                table: "CountPlanLines",
                column: "BalanceId");

            migrationBuilder.CreateIndex(
                name: "IX_CountPlanLines_CountPlanId_BalanceId",
                schema: "counting",
                table: "CountPlanLines",
                columns: new[] { "CountPlanId", "BalanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_CountPlanLines_IdempotencyKey",
                schema: "counting",
                table: "CountPlanLines",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CountPlanLines_WorkTaskId",
                schema: "counting",
                table: "CountPlanLines",
                column: "WorkTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_CountPlans_IdempotencyKey",
                schema: "counting",
                table: "CountPlans",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CountPlans_WarehouseId_Reference",
                schema: "counting",
                table: "CountPlans",
                columns: new[] { "WarehouseId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CountPlans_WarehouseId_Status_CreatedAt",
                schema: "counting",
                table: "CountPlans",
                columns: new[] { "WarehouseId", "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CountPlanLines",
                schema: "counting");

            migrationBuilder.DropTable(
                name: "CountPlans",
                schema: "counting");
        }
    }
}
