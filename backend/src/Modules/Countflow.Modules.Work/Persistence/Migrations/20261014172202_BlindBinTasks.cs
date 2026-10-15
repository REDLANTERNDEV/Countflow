using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Work.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class BlindBinTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_PositiveQuantity",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.AlterColumn<Guid>(
                name: "ItemId",
                schema: "work",
                table: "WorkTasks",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<Guid>(
                name: "BinCountClaimId",
                schema: "work",
                table: "WorkTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BinCountVersion",
                schema: "work",
                table: "WorkTasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_BinCountClaim",
                schema: "work",
                table: "WorkTasks",
                sql: "(\"BinCountVersion\" IS NULL) = (\"BinCountClaimId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_PositiveQuantity",
                schema: "work",
                table: "WorkTasks",
                sql: "\"Kind\" = 6 AND \"Quantity\" = 0 AND \"ItemId\" IS NULL AND \"LotId\" IS NULL AND \"LicensePlateId\" IS NULL OR \"Kind\" <> 6 AND \"Quantity\" > 0 AND \"ItemId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_BinCountClaim",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropCheckConstraint(
                name: "CK_WorkTasks_PositiveQuantity",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "BinCountClaimId",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.DropColumn(
                name: "BinCountVersion",
                schema: "work",
                table: "WorkTasks");

            migrationBuilder.AlterColumn<Guid>(
                name: "ItemId",
                schema: "work",
                table: "WorkTasks",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_WorkTasks_PositiveQuantity",
                schema: "work",
                table: "WorkTasks",
                sql: "\"Quantity\" > 0");
        }
    }
}
