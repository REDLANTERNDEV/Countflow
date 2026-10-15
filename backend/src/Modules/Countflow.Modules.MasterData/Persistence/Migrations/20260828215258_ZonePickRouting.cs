using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.MasterData.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ZonePickRouting : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "HasRearCrossAisle",
                schema: "masterdata",
                table: "Zones",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "PickRouteStrategy",
                schema: "masterdata",
                table: "Zones",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "route.sequence_ordinal");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HasRearCrossAisle",
                schema: "masterdata",
                table: "Zones");

            migrationBuilder.DropColumn(
                name: "PickRouteStrategy",
                schema: "masterdata",
                table: "Zones");
        }
    }
}
