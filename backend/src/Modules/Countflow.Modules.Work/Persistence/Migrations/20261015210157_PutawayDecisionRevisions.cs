using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PutawayDecisionRevisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "PutawayAdviceStale",
                schema: "work",
                table: "WorkTasks",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "PutawayRevision",
                schema: "work",
                table: "WorkTasks",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.CreateTable(
                name: "PutawayDecisionRevisions",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PreviousDecisionTraceId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuggestedLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    ContentsJson = table.Column<string>(type: "jsonb", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PutawayDecisionRevisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PutawayDecisionRevisions_WorkTasks_WorkTaskId",
                        column: x => x.WorkTaskId,
                        principalSchema: "work",
                        principalTable: "WorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PutawayDecisionRevisions_WorkTaskId_IdempotencyKey",
                schema: "work",
                table: "PutawayDecisionRevisions",
                columns: new[] { "WorkTaskId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PutawayDecisionRevisions_WorkTaskId_Revision",
                schema: "work",
                table: "PutawayDecisionRevisions",
                columns: new[] { "WorkTaskId", "Revision" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PutawayDecisionRevisions",
                schema: "work");

            migrationBuilder.DropColumn(
                name: "PutawayAdviceStale",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "PutawayRevision",
                schema: "work",
                table: "WorkTasks");
        }
    }
}
