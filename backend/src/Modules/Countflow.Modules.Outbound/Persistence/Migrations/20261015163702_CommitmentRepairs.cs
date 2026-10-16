using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Outbound.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CommitmentRepairs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "SupersededByRepairId",
                schema: "outbound",
                table: "OrderLineAllocations",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "OrderLineRepairs",
                schema: "outbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    RequestedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AllocationIds = table.Column<Guid[]>(type: "uuid[]", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WithdrawalComplete = table.Column<bool>(type: "boolean", nullable: false),
                    AllocationPlanJson = table.Column<string>(type: "text", nullable: true),
                    ReservationsReleased = table.Column<int>(type: "integer", nullable: false),
                    TasksCancelled = table.Column<int>(type: "integer", nullable: false),
                    AllocationsRaised = table.Column<int>(type: "integer", nullable: false),
                    TasksRaised = table.Column<int>(type: "integer", nullable: false),
                    ShortfallQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLineRepairs", x => x.Id);
                    table.ForeignKey(
                        name: "FK_OrderLineRepairs_SalesOrderLines_SalesOrderLineId",
                        column: x => x.SalesOrderLineId,
                        principalSchema: "outbound",
                        principalTable: "SalesOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderMutationFences",
                schema: "outbound",
                columns: table => new
                {
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderMutationFences", x => x.OrderId);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLineRepairs_IdempotencyKey",
                schema: "outbound",
                table: "OrderLineRepairs",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderLineRepairs_SalesOrderLineId",
                schema: "outbound",
                table: "OrderLineRepairs",
                column: "SalesOrderLineId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderLineRepairs",
                schema: "outbound");

            migrationBuilder.DropTable(
                name: "OrderMutationFences",
                schema: "outbound");

            migrationBuilder.DropColumn(
                name: "SupersededByRepairId",
                schema: "outbound",
                table: "OrderLineAllocations");
        }
    }
}
