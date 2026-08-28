using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Outbound.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReturnsAndDisposition : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "ReturnedQuantity",
                schema: "outbound",
                table: "SalesOrderLines",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.CreateTable(
                name: "Returns",
                schema: "outbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    SalesOrderReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CustomerName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ReceivedAtLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Returns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ReturnLines",
                schema: "outbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    SalesOrderLineId = table.Column<Guid>(type: "uuid", nullable: true),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    LotNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    LicensePlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReceiptMovementId = table.Column<Guid>(type: "uuid", nullable: false),
                    InspectionTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    DispositionedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReturnLines", x => x.Id);
                    table.CheckConstraint("CK_ReturnLines_DispositionWithinLine", "\"DispositionedQuantity\" >= 0 AND \"DispositionedQuantity\" <= \"Quantity\"");
                    table.CheckConstraint("CK_ReturnLines_PositiveQuantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_ReturnLines_Returns_ReturnId",
                        column: x => x.ReturnId,
                        principalSchema: "outbound",
                        principalTable: "Returns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReturnLineDispositions",
                schema: "outbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReturnLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    Disposition = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: false),
                    WasOverridden = table.Column<bool>(type: "boolean", nullable: false),
                    MovementId = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DecidedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReturnLineDispositions", x => x.Id);
                    table.CheckConstraint("CK_ReturnLineDispositions_PositiveQuantity", "\"Quantity\" > 0");
                    table.ForeignKey(
                        name: "FK_ReturnLineDispositions_ReturnLines_ReturnLineId",
                        column: x => x.ReturnLineId,
                        principalSchema: "outbound",
                        principalTable: "ReturnLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnLineDispositions_DecisionTraceId",
                schema: "outbound",
                table: "ReturnLineDispositions",
                column: "DecisionTraceId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnLineDispositions_IdempotencyKey",
                schema: "outbound",
                table: "ReturnLineDispositions",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReturnLineDispositions_ReturnLineId_DecidedAt",
                schema: "outbound",
                table: "ReturnLineDispositions",
                columns: new[] { "ReturnLineId", "DecidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReturnLines_ItemId",
                schema: "outbound",
                table: "ReturnLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_ReturnLines_ReceiptMovementId",
                schema: "outbound",
                table: "ReturnLines",
                column: "ReceiptMovementId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReturnLines_ReturnId_LineNumber",
                schema: "outbound",
                table: "ReturnLines",
                columns: new[] { "ReturnId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ReturnLines_SalesOrderLineId",
                schema: "outbound",
                table: "ReturnLines",
                column: "SalesOrderLineId");

            migrationBuilder.CreateIndex(
                name: "IX_Returns_SalesOrderId",
                schema: "outbound",
                table: "Returns",
                column: "SalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_Returns_Status",
                schema: "outbound",
                table: "Returns",
                column: "Status");

            migrationBuilder.CreateIndex(
                name: "IX_Returns_WarehouseId_Reference",
                schema: "outbound",
                table: "Returns",
                columns: new[] { "WarehouseId", "Reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReturnLineDispositions",
                schema: "outbound");

            migrationBuilder.DropTable(
                name: "ReturnLines",
                schema: "outbound");

            migrationBuilder.DropTable(
                name: "Returns",
                schema: "outbound");

            migrationBuilder.DropColumn(
                name: "ReturnedQuantity",
                schema: "outbound",
                table: "SalesOrderLines");
        }
    }
}
