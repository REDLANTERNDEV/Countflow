using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PutawayCompletionIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "PendingPutawayCompletionId",
                schema: "work",
                table: "WorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PutawayCompletionIntents",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(124)", maxLength: 124, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    State = table.Column<int>(type: "integer", nullable: false),
                    PreparedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ErrorJson = table.Column<string>(type: "jsonb", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PutawayCompletionIntents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PutawayCompletionIntents_WorkTasks_WorkTaskId",
                        column: x => x.WorkTaskId,
                        principalSchema: "work",
                        principalTable: "WorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_PendingPutawayCompletionId",
                schema: "work",
                table: "WorkTasks",
                column: "PendingPutawayCompletionId");

            migrationBuilder.CreateIndex(
                name: "IX_PutawayCompletionIntents_WorkTaskId_IdempotencyKey",
                schema: "work",
                table: "PutawayCompletionIntents",
                columns: new[] { "WorkTaskId", "IdempotencyKey" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_WorkTasks_PutawayCompletionIntents_PendingPutawayCompletion~",
                schema: "work",
                table: "WorkTasks",
                column: "PendingPutawayCompletionId",
                principalSchema: "work",
                principalTable: "PutawayCompletionIntents",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_WorkTasks_PutawayCompletionIntents_PendingPutawayCompletion~",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropTable(
                name: "PutawayCompletionIntents",
                schema: "work");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_PendingPutawayCompletionId",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "PendingPutawayCompletionId",
                schema: "work",
                table: "WorkTasks");
        }
    }
}
