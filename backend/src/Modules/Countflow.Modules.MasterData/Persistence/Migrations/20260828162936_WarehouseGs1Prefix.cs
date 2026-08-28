using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.MasterData.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class WarehouseGs1Prefix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateSequence(
                name: "sscc_serial",
                schema: "masterdata");

            migrationBuilder.AddColumn<string>(
                name: "Gs1CompanyPrefix",
                schema: "masterdata",
                table: "Warehouses",
                type: "character varying(10)",
                maxLength: 10,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "SsccExtensionDigit",
                schema: "masterdata",
                table: "Warehouses",
                type: "integer",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Gs1CompanyPrefix",
                schema: "masterdata",
                table: "Warehouses");

            migrationBuilder.DropColumn(
                name: "SsccExtensionDigit",
                schema: "masterdata",
                table: "Warehouses");

            migrationBuilder.DropSequence(
                name: "sscc_serial",
                schema: "masterdata");
        }
    }
}
