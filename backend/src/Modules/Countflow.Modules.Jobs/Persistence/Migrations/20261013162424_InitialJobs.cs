using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Jobs.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "jobs");

            migrationBuilder.CreateTable(
                name: "JobStates",
                schema: "jobs",
                columns: table => new
                {
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    NextRunAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    PendingRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    PendingScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastRunId = table.Column<Guid>(type: "uuid", nullable: true),
                    LastStartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastFinishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConsecutiveFailures = table.Column<int>(type: "integer", nullable: false),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_JobStates", x => x.Name);
                    table.CheckConstraint("CK_JobStates_PendingRunHasSchedule", "(\"PendingRunId\" IS NULL) = (\"PendingScheduledAt\" IS NULL)");
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "JobStates",
                schema: "jobs");
        }
    }
}
