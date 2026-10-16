using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PutawayReceiptIntents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "PendingReceiptCount",
                schema: "work",
                table: "WorkTasks",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "PutawayReceiptLanes",
                schema: "work",
                columns: table => new
                {
                    Sscc = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: false),
                    LicensePlateId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PutawayReceiptLanes", x => x.Sscc);
                });

            migrationBuilder.CreateTable(
                name: "PutawayReceiptIntents",
                schema: "work",
                columns: table => new
                {
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Fingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Sscc = table.Column<string>(type: "character varying(18)", maxLength: 18, nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PreparedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsPending = table.Column<bool>(type: "boolean", nullable: false),
                    AcknowledgedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PutawayReceiptIntents", x => x.IdempotencyKey);
                    table.ForeignKey(
                        name: "FK_PutawayReceiptIntents_PutawayReceiptLanes_Sscc",
                        column: x => x.Sscc,
                        principalSchema: "work",
                        principalTable: "PutawayReceiptLanes",
                        principalColumn: "Sscc",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_PendingReceiptCount",
                schema: "work",
                table: "WorkTasks",
                sql: "\"PendingReceiptCount\" >= 0");

            migrationBuilder.CreateIndex(
                name: "IX_PutawayReceiptIntents_Sscc_FromLocationId_IsPending",
                schema: "work",
                table: "PutawayReceiptIntents",
                columns: new[] { "Sscc", "FromLocationId", "IsPending" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PutawayReceiptIntents",
                schema: "work");

            migrationBuilder.DropTable(
                name: "PutawayReceiptLanes",
                schema: "work");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_PendingReceiptCount",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "PendingReceiptCount",
                schema: "work",
                table: "WorkTasks");
        }
    }
}
