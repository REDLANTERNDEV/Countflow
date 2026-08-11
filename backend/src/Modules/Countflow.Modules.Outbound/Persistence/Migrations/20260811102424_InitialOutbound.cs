using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Outbound.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialOutbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "outbound");

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                schema: "outbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: true),
                    CustomerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    StagingLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ShipBy = table.Column<DateOnly>(type: "date", nullable: true),
                    WaveId = table.Column<Guid>(type: "uuid", nullable: true),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PackedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PackedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DespatchedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DespatchedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    DespatchIdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CancelledReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Waves",
                schema: "outbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Waves", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderLines",
                schema: "outbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    RequiredLotId = table.Column<Guid>(type: "uuid", nullable: true),
                    AllocatedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderLines", x => x.Id);
                    table.CheckConstraint("CK_SalesOrderLines_AllocationWithinOrder", "\"AllocatedQuantity\" >= 0 AND \"AllocatedQuantity\" <= \"OrderedQuantity\"");
                    table.CheckConstraint("CK_SalesOrderLines_PositiveOrdered", "\"OrderedQuantity\" > 0");
                    table.ForeignKey(
                        name: "FK_SalesOrderLines_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalSchema: "outbound",
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "OrderLineAllocations",
                schema: "outbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReleaseIdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    StockReservationId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LocationCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    LotNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    AllocatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_OrderLineAllocations", x => x.Id);
                    table.CheckConstraint("CK_OrderLineAllocations_PositiveQuantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_OrderLineAllocations_SalesOrderLines_SalesOrderLineId",
                        column: x => x.SalesOrderLineId,
                        principalSchema: "outbound",
                        principalTable: "SalesOrderLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLineAllocations_SalesOrderLineId_ReleaseIdempotencyKey",
                schema: "outbound",
                table: "OrderLineAllocations",
                columns: new[] { "SalesOrderLineId", "ReleaseIdempotencyKey" });

            migrationBuilder.CreateIndex(
                name: "IX_OrderLineAllocations_StockReservationId",
                schema: "outbound",
                table: "OrderLineAllocations",
                column: "StockReservationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_OrderLineAllocations_WorkTaskId",
                schema: "outbound",
                table: "OrderLineAllocations",
                column: "WorkTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_ItemId",
                schema: "outbound",
                table: "SalesOrderLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderLines_SalesOrderId_LineNumber",
                schema: "outbound",
                table: "SalesOrderLines",
                columns: new[] { "SalesOrderId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_Status_Priority_ShipBy",
                schema: "outbound",
                table: "SalesOrders",
                columns: new[] { "Status", "Priority", "ShipBy" });

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_WarehouseId_Reference",
                schema: "outbound",
                table: "SalesOrders",
                columns: new[] { "WarehouseId", "Reference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_WaveId",
                schema: "outbound",
                table: "SalesOrders",
                column: "WaveId");

            migrationBuilder.CreateIndex(
                name: "IX_Waves_Status",
                schema: "outbound",
                table: "Waves",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Waves_WarehouseId_Reference",
                schema: "outbound",
                table: "Waves",
                columns: new[] { "WarehouseId", "Reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "OrderLineAllocations",
                schema: "outbound");

            migrationBuilder.DropTable(
                name: "Waves",
                schema: "outbound");

            migrationBuilder.DropTable(
                name: "SalesOrderLines",
                schema: "outbound");

            migrationBuilder.DropTable(
                name: "SalesOrders",
                schema: "outbound");
        }
    }
}
