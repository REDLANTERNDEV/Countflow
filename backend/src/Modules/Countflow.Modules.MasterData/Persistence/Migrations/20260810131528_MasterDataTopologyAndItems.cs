using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.MasterData.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MasterDataTopologyAndItems : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Areas",
                schema: "masterdata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Areas", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Areas_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "masterdata",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BusinessPartners",
                schema: "masterdata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Roles = table.Column<int>(type: "integer", nullable: false),
                    Gln = table.Column<string>(type: "character varying(13)", maxLength: 13, nullable: true),
                    City = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CountryCode = table.Column<string>(type: "character varying(2)", maxLength: 2, nullable: true),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BusinessPartners", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Items",
                schema: "masterdata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sku = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Description = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: false),
                    BaseUom = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    AbcClass = table.Column<int>(type: "integer", nullable: false),
                    TemperatureClass = table.Column<int>(type: "integer", nullable: false),
                    IsLotTracked = table.Column<bool>(type: "boolean", nullable: false),
                    IsSerialTracked = table.Column<bool>(type: "boolean", nullable: false),
                    IsExpiryTracked = table.Column<bool>(type: "boolean", nullable: false),
                    ShelfLifeDays = table.Column<int>(type: "integer", nullable: true),
                    MinimumRemainingShelfLifePercent = table.Column<int>(type: "integer", nullable: true),
                    IsHazardous = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Items", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Zones",
                schema: "masterdata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AreaId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TemperatureClass = table.Column<int>(type: "integer", nullable: false),
                    PickSequence = table.Column<int>(type: "integer", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Zones", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Zones_Areas_AreaId",
                        column: x => x.AreaId,
                        principalSchema: "masterdata",
                        principalTable: "Areas",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "PackConfigurations",
                schema: "masterdata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Level = table.Column<int>(type: "integer", nullable: false),
                    BaseUnitQuantity = table.Column<int>(type: "integer", nullable: false),
                    Gtin = table.Column<string>(type: "character(14)", fixedLength: true, maxLength: 14, nullable: true),
                    LengthMm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    WidthMm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    HeightMm = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: true),
                    GrossWeightKg = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    IsReceivingUnit = table.Column<bool>(type: "boolean", nullable: false),
                    IsPickUnit = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PackConfigurations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PackConfigurations_Items_ItemId",
                        column: x => x.ItemId,
                        principalSchema: "masterdata",
                        principalTable: "Items",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Locations",
                schema: "masterdata",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneId = table.Column<Guid>(type: "uuid", nullable: true),
                    Code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Aisle = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Bay = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Level = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    Position = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    PickSequence = table.Column<int>(type: "integer", nullable: false),
                    MaxVolumeM3 = table.Column<decimal>(type: "numeric(12,6)", precision: 12, scale: 6, nullable: true),
                    MaxWeightKg = table.Column<decimal>(type: "numeric(12,3)", precision: 12, scale: 3, nullable: true),
                    MaxDistinctItems = table.Column<int>(type: "integer", nullable: true),
                    IsPickable = table.Column<bool>(type: "boolean", nullable: false),
                    IsReplenishable = table.Column<bool>(type: "boolean", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Locations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Locations_Warehouses_WarehouseId",
                        column: x => x.WarehouseId,
                        principalSchema: "masterdata",
                        principalTable: "Warehouses",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Locations_Zones_ZoneId",
                        column: x => x.ZoneId,
                        principalSchema: "masterdata",
                        principalTable: "Zones",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Areas_WarehouseId_Code",
                schema: "masterdata",
                table: "Areas",
                columns: new[] { "WarehouseId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BusinessPartners_Code",
                schema: "masterdata",
                table: "BusinessPartners",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Items_AbcClass",
                schema: "masterdata",
                table: "Items",
                column: "AbcClass");

            migrationBuilder.CreateIndex(
                name: "IX_Items_Sku",
                schema: "masterdata",
                table: "Items",
                column: "Sku",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Locations_Code",
                schema: "masterdata",
                table: "Locations",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Locations_WarehouseId_Type",
                schema: "masterdata",
                table: "Locations",
                columns: new[] { "WarehouseId", "Type" });

            migrationBuilder.CreateIndex(
                name: "IX_Locations_ZoneId_PickSequence",
                schema: "masterdata",
                table: "Locations",
                columns: new[] { "ZoneId", "PickSequence" });

            migrationBuilder.CreateIndex(
                name: "IX_PackConfigurations_Gtin",
                schema: "masterdata",
                table: "PackConfigurations",
                column: "Gtin",
                unique: true,
                filter: "\"Gtin\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_PackConfigurations_ItemId_Level",
                schema: "masterdata",
                table: "PackConfigurations",
                columns: new[] { "ItemId", "Level" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Zones_AreaId_Code",
                schema: "masterdata",
                table: "Zones",
                columns: new[] { "AreaId", "Code" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BusinessPartners",
                schema: "masterdata");

            migrationBuilder.DropTable(
                name: "Locations",
                schema: "masterdata");

            migrationBuilder.DropTable(
                name: "PackConfigurations",
                schema: "masterdata");

            migrationBuilder.DropTable(
                name: "Zones",
                schema: "masterdata");

            migrationBuilder.DropTable(
                name: "Items",
                schema: "masterdata");

            migrationBuilder.DropTable(
                name: "Areas",
                schema: "masterdata");
        }
    }
}
