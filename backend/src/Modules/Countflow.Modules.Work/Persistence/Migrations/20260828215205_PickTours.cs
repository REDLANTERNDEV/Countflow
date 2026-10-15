using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PickTours : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PickTourId",
                schema: "work",
                table: "WorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TourSequence",
                schema: "work",
                table: "WorkTasks",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PickTours",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedToUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    ClaimedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StopCount = table.Column<int>(type: "integer", nullable: false),
                    TravelSteps = table.Column<int>(type: "integer", nullable: true),
                    BaselineTravelSteps = table.Column<int>(type: "integer", nullable: true),
                    AisleChanges = table.Column<int>(type: "integer", nullable: true),
                    ReleasedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReleasedStops = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickTours", x => x.Id);
                    table.CheckConstraint("CK_PickTours_PositiveStops", "\"StopCount\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "PickTourSegments",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    PickTourId = table.Column<Guid>(type: "uuid", nullable: false),
                    ZoneCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    RequestedStrategy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AppliedStrategy = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    FellBackBecause = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Stops = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PickTourSegments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PickTourSegments_PickTours_PickTourId",
                        column: x => x.PickTourId,
                        principalSchema: "work",
                        principalTable: "PickTours",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_PickTourId_TourSequence",
                schema: "work",
                table: "WorkTasks",
                columns: new[] { "PickTourId", "TourSequence" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_TourSequenceBelongsToTour",
                schema: "work",
                table: "WorkTasks",
                sql: "(\"PickTourId\" IS NULL) = (\"TourSequence\" IS NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_PickTourSegments_PickTourId",
                schema: "work",
                table: "PickTourSegments",
                column: "PickTourId");

            migrationBuilder.CreateIndex(
                name: "IX_PickTours_AssignedToUserId_ClaimedAt",
                schema: "work",
                table: "PickTours",
                columns: new[] { "AssignedToUserId", "ClaimedAt" });

            migrationBuilder.AddForeignKey(
                name: "FK_WorkTasks_PickTours_PickTourId",
                schema: "work",
                table: "WorkTasks",
                column: "PickTourId",
                principalSchema: "work",
                principalTable: "PickTours",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkTasks_PickTours_PickTourId",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropTable(
                name: "PickTourSegments",
                schema: "work");

            migrationBuilder.DropTable(
                name: "PickTours",
                schema: "work");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_PickTourId_TourSequence",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_TourSequenceBelongsToTour",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "PickTourId",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "TourSequence",
                schema: "work",
                table: "WorkTasks");
        }
    }
}
