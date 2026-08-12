using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Replay.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialReplay : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "replay");

            migrationBuilder.CreateTable(
                name: "ReplayRuns",
                schema: "replay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reference = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    CandidateRuleSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateRuleSetName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    CandidateRuleSetVersion = table.Column<int>(type: "integer", nullable: false),
                    BaselineRuleSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    BaselineRuleSetName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BaselineRuleSetVersion = table.Column<int>(type: "integer", nullable: false),
                    FromDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ToDate = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ReplayedCount = table.Column<int>(type: "integer", nullable: false),
                    ExactCount = table.Column<int>(type: "integer", nullable: false),
                    ApproximateCount = table.Column<int>(type: "integer", nullable: false),
                    UnreplayableCount = table.Column<int>(type: "integer", nullable: false),
                    ChangedCount = table.Column<int>(type: "integer", nullable: false),
                    ReorderedCount = table.Column<int>(type: "integer", nullable: false),
                    GainedCount = table.Column<int>(type: "integer", nullable: false),
                    LostCount = table.Column<int>(type: "integer", nullable: false),
                    ElapsedMilliseconds = table.Column<long>(type: "bigint", nullable: false),
                    InfluenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayRuns", x => x.Id);
                    table.CheckConstraint("CK_ReplayRuns_FidelitySumsToReplayed", "\"ExactCount\" + \"ApproximateCount\" + \"UnreplayableCount\" = \"ReplayedCount\"");
                    table.CheckConstraint("CK_ReplayRuns_Window", "\"FromDate\" <= \"ToDate\"");
                });

            migrationBuilder.CreateTable(
                name: "ReplayRunDecisions",
                schema: "replay",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReplayRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Fidelity = table.Column<int>(type: "integer", nullable: false),
                    FidelityNote = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Outcome = table.Column<int>(type: "integer", nullable: false),
                    RecordedKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BaselineKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CandidateKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    BaselineScore = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    CandidateScore = table.Column<decimal>(type: "numeric(9,6)", precision: 9, scale: 6, nullable: false),
                    CandidateRankUnderBaseline = table.Column<int>(type: "integer", nullable: false),
                    BaselineRankUnderCandidate = table.Column<int>(type: "integer", nullable: false),
                    ConsideredCount = table.Column<int>(type: "integer", nullable: false),
                    BaselineAdmissibleCount = table.Column<int>(type: "integer", nullable: false),
                    CandidateAdmissibleCount = table.Column<int>(type: "integer", nullable: false),
                    BaselineInfluenceJson = table.Column<string>(type: "jsonb", nullable: false),
                    CandidateInfluenceJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReplayRunDecisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReplayRunDecisions_ReplayRuns_ReplayRunId",
                        column: x => x.ReplayRunId,
                        principalSchema: "replay",
                        principalTable: "ReplayRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReplayRunDecisions_DecisionTraceId",
                schema: "replay",
                table: "ReplayRunDecisions",
                column: "DecisionTraceId");

            migrationBuilder.CreateIndex(
                name: "IX_ReplayRunDecisions_ReplayRunId_Outcome",
                schema: "replay",
                table: "ReplayRunDecisions",
                columns: new[] { "ReplayRunId", "Outcome" });

            migrationBuilder.CreateIndex(
                name: "IX_ReplayRuns_CandidateRuleSetId",
                schema: "replay",
                table: "ReplayRuns",
                column: "CandidateRuleSetId");

            migrationBuilder.CreateIndex(
                name: "IX_ReplayRuns_WarehouseId_Kind_CreatedAt",
                schema: "replay",
                table: "ReplayRuns",
                columns: new[] { "WarehouseId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ReplayRuns_WarehouseId_Reference",
                schema: "replay",
                table: "ReplayRuns",
                columns: new[] { "WarehouseId", "Reference" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReplayRunDecisions",
                schema: "replay");

            migrationBuilder.DropTable(
                name: "ReplayRuns",
                schema: "replay");
        }
    }
}
