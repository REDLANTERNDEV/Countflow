using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Rules.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "rules");

            migrationBuilder.CreateTable(
                name: "DecisionTraces",
                schema: "rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    RuleSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleSetName = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    RuleSetVersion = table.Column<int>(type: "integer", nullable: false),
                    DecidedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConsideredCount = table.Column<int>(type: "integer", nullable: false),
                    AdmissibleCount = table.Column<int>(type: "integer", nullable: false),
                    ElapsedMicroseconds = table.Column<long>(type: "bigint", nullable: false),
                    SelectedCandidateId = table.Column<Guid>(type: "uuid", nullable: true),
                    SelectedCandidateKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    InputsJson = table.Column<string>(type: "jsonb", nullable: false),
                    IsPreview = table.Column<bool>(type: "boolean", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionTraces", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RuleSets",
                schema: "rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleSets", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DecisionTraceCandidates",
                schema: "rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateId = table.Column<Guid>(type: "uuid", nullable: false),
                    CandidateKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Rank = table.Column<int>(type: "integer", nullable: false),
                    IsAdmissible = table.Column<bool>(type: "boolean", nullable: false),
                    Score = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    ExcludedByJson = table.Column<string>(type: "jsonb", nullable: true),
                    ScoresJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DecisionTraceCandidates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DecisionTraceCandidates_DecisionTraces_DecisionTraceId",
                        column: x => x.DecisionTraceId,
                        principalSchema: "rules",
                        principalTable: "DecisionTraces",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RuleDefinitions",
                schema: "rules",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RuleSetId = table.Column<Guid>(type: "uuid", nullable: false),
                    Code = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Role = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    Weight = table.Column<decimal>(type: "numeric(9,4)", precision: 9, scale: 4, nullable: false),
                    Parameters = table.Column<string>(type: "jsonb", nullable: true),
                    IsEnabled = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RuleDefinitions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RuleDefinitions_RuleSets_RuleSetId",
                        column: x => x.RuleSetId,
                        principalSchema: "rules",
                        principalTable: "RuleSets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraceCandidates_DecisionTraceId_Rank",
                schema: "rules",
                table: "DecisionTraceCandidates",
                columns: new[] { "DecisionTraceId", "Rank" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraces_DecidedAt",
                schema: "rules",
                table: "DecisionTraces",
                column: "DecidedAt");

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraces_Kind_DecidedAt",
                schema: "rules",
                table: "DecisionTraces",
                columns: new[] { "Kind", "DecidedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DecisionTraces_RuleSetId",
                schema: "rules",
                table: "DecisionTraces",
                column: "RuleSetId");

            migrationBuilder.CreateIndex(
                name: "IX_RuleDefinitions_RuleSetId_Code",
                schema: "rules",
                table: "RuleDefinitions",
                columns: new[] { "RuleSetId", "Code" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RuleSets_Kind_WarehouseId",
                schema: "rules",
                table: "RuleSets",
                columns: new[] { "Kind", "WarehouseId" },
                unique: true,
                filter: "\"Status\" = 2")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_RuleSets_Name_Version",
                schema: "rules",
                table: "RuleSets",
                columns: new[] { "Name", "Version" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DecisionTraceCandidates",
                schema: "rules");

            migrationBuilder.DropTable(
                name: "RuleDefinitions",
                schema: "rules");

            migrationBuilder.DropTable(
                name: "DecisionTraces",
                schema: "rules");

            migrationBuilder.DropTable(
                name: "RuleSets",
                schema: "rules");
        }
    }
}
