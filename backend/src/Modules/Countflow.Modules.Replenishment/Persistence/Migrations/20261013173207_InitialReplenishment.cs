using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Countflow.Modules.Replenishment.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialReplenishment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "replenishment");

            migrationBuilder.CreateTable(
                name: "PickFacePolicies",
                schema: "replenishment",
                columns: table => new
                {
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Minimum = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Maximum = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    PendingPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastDecisionTraceId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastCheckedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastShortfall = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickFacePolicies", x => x.LocationId);
                    table.CheckConstraint("CK_PickFacePolicies_Levels", "\"Minimum\" >= 0 AND \"Maximum\" > \"Minimum\"");
                });

            migrationBuilder.CreateTable(
                name: "Plans",
                schema: "replenishment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Plans", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Lines",
                schema: "replenishment",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PlanId = table.Column<Guid>(type: "uuid", nullable: false),
                    BalanceId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    LicensePlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReservationId = table.Column<Guid>(type: "uuid", nullable: true),
                    WorkTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    Refused = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lines", x => x.Id);
                    table.CheckConstraint("CK_ReplenishmentLines_Positive", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_Lines_Plans_PlanId",
                        column: x => x.PlanId,
                        principalSchema: "replenishment",
                        principalTable: "Plans",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Lines_PlanId_BalanceId",
                schema: "replenishment",
                table: "Lines",
                columns: new[] { "PlanId", "BalanceId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PickFacePolicies_WarehouseId",
                schema: "replenishment",
                table: "PickFacePolicies",
                column: "WarehouseId");

            migrationBuilder.CreateIndex(
                name: "IX_Plans_LocationId_Status",
                schema: "replenishment",
                table: "Plans",
                columns: new[] { "LocationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_Plans_Number",
                schema: "replenishment",
                table: "Plans",
                column: "Number",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Lines",
                schema: "replenishment");

            migrationBuilder.DropTable(
                name: "PickFacePolicies",
                schema: "replenishment");

            migrationBuilder.DropTable(
                name: "Plans",
                schema: "replenishment");
        }
    }
}
