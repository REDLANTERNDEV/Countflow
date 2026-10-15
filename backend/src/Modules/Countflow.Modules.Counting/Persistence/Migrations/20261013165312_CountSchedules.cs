using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Counting.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class CountSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CountSchedules",
                schema: "counting",
                columns: table => new
                {
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    IntervalMinutes = table.Column<int>(type: "integer", nullable: false),
                    MaxTasks = table.Column<int>(type: "integer", nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    PendingRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    PendingScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastPlanId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Revision = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CountSchedules", x => x.WarehouseId);
                    table.CheckConstraint("CK_CountSchedules_Budget", "\"MaxTasks\" BETWEEN 1 AND 200");
                    table.CheckConstraint("CK_CountSchedules_Interval", "\"IntervalMinutes\" BETWEEN 1 AND 10080");
                    table.CheckConstraint("CK_CountSchedules_Pending", "(\"PendingRunId\" IS NULL) = (\"PendingScheduledAt\" IS NULL)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CountSchedules",
                schema: "counting");
        }
    }
}
