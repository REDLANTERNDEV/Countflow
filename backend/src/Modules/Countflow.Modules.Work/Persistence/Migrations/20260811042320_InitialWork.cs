using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialWork : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "work");

            migrationBuilder.CreateTable(
                name: "WorkTasks",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Priority = table.Column<int>(type: "integer", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    FromLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SuggestedLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    LicensePlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: true),
                    Condition = table.Column<int>(type: "integer", nullable: false),
                    SourceReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AssignedToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    AssignedToUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAtLocationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    CompletedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    WasOverridden = table.Column<bool>(type: "boolean", nullable: false),
                    OverrideReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CompletionIdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    CancelledReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTasks", x => x.Id);
                    table.CheckConstraint("CK_WorkTasks_CompletedHasLocation", "\"Status\" <> 3\nOR (\"CompletedAtLocationId\" IS NOT NULL AND \"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_WorkTasks_PositiveQuantity", "\"Quantity\" > 0");
                });

            migrationBuilder.CreateTable(
                name: "WorkTaskSources",
                schema: "work",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WorkTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AddedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WorkTaskSources", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WorkTaskSources_WorkTasks_WorkTaskId",
                        column: x => x.WorkTaskId,
                        principalSchema: "work",
                        principalTable: "WorkTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskSources_IdempotencyKey",
                schema: "work",
                table: "WorkTaskSources",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WorkTaskSources_WorkTaskId",
                schema: "work",
                table: "WorkTaskSources",
                column: "WorkTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_AssignedToUserId_Status",
                schema: "work",
                table: "WorkTasks",
                columns: new[] { "AssignedToUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_DecisionTraceId",
                schema: "work",
                table: "WorkTasks",
                column: "DecisionTraceId");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_LicensePlateId_FromLocationId_Kind",
                schema: "work",
                table: "WorkTasks",
                columns: new[] { "LicensePlateId", "FromLocationId", "Kind" },
                unique: true,
                filter: "\"LicensePlateId\" IS NOT NULL AND \"Status\" IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_SourceReference",
                schema: "work",
                table: "WorkTasks",
                column: "SourceReference");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_WarehouseId_Status_Priority_CreatedAt",
                schema: "work",
                table: "WorkTasks",
                columns: new[] { "WarehouseId", "Status", "Priority", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "WorkTaskSources",
                schema: "work");

            migrationBuilder.DropTable(
                name: "WorkTasks",
                schema: "work");
        }
    }
}
