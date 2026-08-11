using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Inbound.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInbound : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inbound");

            migrationBuilder.CreateTable(
                name: "Asns",
                schema: "inbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SupplierId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupplierName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    ExpectedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CarrierReference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Asns", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AsnLines",
                schema: "inbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AsnId = table.Column<Guid>(type: "uuid", nullable: false),
                    LineNumber = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpectedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ReceivedQuantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    ExpectedLotNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ExpectedExpiryDate = table.Column<DateOnly>(type: "date", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsnLines", x => x.Id);
                    table.CheckConstraint("CK_AsnLines_NonNegativeReceived", "\"ReceivedQuantity\" >= 0");
                    table.CheckConstraint("CK_AsnLines_PositiveExpected", "\"ExpectedQuantity\" > 0");
                    table.ForeignKey(
                        name: "FK_AsnLines_Asns_AsnId",
                        column: x => x.AsnId,
                        principalSchema: "inbound",
                        principalTable: "Asns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AsnReceipts",
                schema: "inbound",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AsnLineId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    MovementId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReceivedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AsnReceipts", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AsnReceipts_AsnLines_AsnLineId",
                        column: x => x.AsnLineId,
                        principalSchema: "inbound",
                        principalTable: "AsnLines",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AsnLines_AsnId_LineNumber",
                schema: "inbound",
                table: "AsnLines",
                columns: new[] { "AsnId", "LineNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsnLines_ItemId",
                schema: "inbound",
                table: "AsnLines",
                column: "ItemId");

            migrationBuilder.CreateIndex(
                name: "IX_AsnReceipts_AsnLineId",
                schema: "inbound",
                table: "AsnReceipts",
                column: "AsnLineId");

            migrationBuilder.CreateIndex(
                name: "IX_AsnReceipts_IdempotencyKey",
                schema: "inbound",
                table: "AsnReceipts",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AsnReceipts_MovementId",
                schema: "inbound",
                table: "AsnReceipts",
                column: "MovementId");

            migrationBuilder.CreateIndex(
                name: "IX_Asns_Status_ExpectedAt",
                schema: "inbound",
                table: "Asns",
                columns: new[] { "Status", "ExpectedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Asns_WarehouseId_Reference",
                schema: "inbound",
                table: "Asns",
                columns: new[] { "WarehouseId", "Reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AsnReceipts",
                schema: "inbound");

            migrationBuilder.DropTable(
                name: "AsnLines",
                schema: "inbound");

            migrationBuilder.DropTable(
                name: "Asns",
                schema: "inbound");
        }
    }
}
