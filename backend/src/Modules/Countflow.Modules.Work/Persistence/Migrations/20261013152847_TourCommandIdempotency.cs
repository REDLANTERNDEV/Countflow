using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class TourCommandIdempotency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClaimIdempotencyKey",
                schema: "work",
                table: "PickTours",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReleaseIdempotencyKey",
                schema: "work",
                table: "PickTours",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_PickTours_AssignedToUserId_ClaimIdempotencyKey",
                schema: "work",
                table: "PickTours",
                columns: new[] { "AssignedToUserId", "ClaimIdempotencyKey" },
                unique: true,
                filter: "\"ClaimIdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_PickTours_AssignedToUserId_ClaimIdempotencyKey",
                schema: "work",
                table: "PickTours");

            migrationBuilder.DropColumn(
                name: "ClaimIdempotencyKey",
                schema: "work",
                table: "PickTours");

            migrationBuilder.DropColumn(
                name: "ReleaseIdempotencyKey",
                schema: "work",
                table: "PickTours");
        }
    }
}
