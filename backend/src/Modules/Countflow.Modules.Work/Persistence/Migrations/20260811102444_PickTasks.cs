using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PickTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_LicensePlateId_FromLocationId_Kind",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.AddColumn<Guid>(
                name: "StockReservationId",
                schema: "work",
                table: "WorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                schema: "work",
                table: "WorkTaskSources",
                type: "character varying(256)",
                maxLength: 256,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(128)",
                oldMaxLength: 128);

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_LicensePlateId_FromLocationId_Kind",
                schema: "work",
                table: "WorkTasks",
                columns: new[] { "LicensePlateId", "FromLocationId", "Kind" },
                unique: true,
                filter: "\"LicensePlateId\" IS NOT NULL AND \"Kind\" = 1 AND \"Status\" IN (1, 2)");

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_StockReservationId",
                schema: "work",
                table: "WorkTasks",
                column: "StockReservationId",
                unique: true,
                filter: "\"StockReservationId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_LicensePlateId_FromLocationId_Kind",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropIndex(
                name: "IX_WorkTasks_StockReservationId",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "StockReservationId",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.AlterColumn<string>(
                name: "IdempotencyKey",
                schema: "work",
                table: "WorkTaskSources",
                type: "character varying(128)",
                maxLength: 128,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(256)",
                oldMaxLength: 256);

            migrationBuilder.CreateIndex(
                name: "IX_WorkTasks_LicensePlateId_FromLocationId_Kind",
                schema: "work",
                table: "WorkTasks",
                columns: new[] { "LicensePlateId", "FromLocationId", "Kind" },
                unique: true,
                filter: "\"LicensePlateId\" IS NOT NULL AND \"Status\" IN (1, 2)");
        }
    }
}
