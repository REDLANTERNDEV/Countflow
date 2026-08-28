using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Countflow.Modules.Labels.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class PalletLabelTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "labels");

            migrationBuilder.CreateTable(
                name: "LabelPrints",
                schema: "labels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    LabelTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    TemplateName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    TemplateVersion = table.Column<int>(type: "integer", nullable: false),
                    SubjectKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SubjectId = table.Column<Guid>(type: "uuid", nullable: true),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsReprint = table.Column<bool>(type: "boolean", nullable: false),
                    PayloadHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    PrintedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelPrints", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LabelTemplates",
                schema: "labels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    WarehouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    WidthDots = table.Column<int>(type: "integer", nullable: false),
                    HeightDots = table.Column<int>(type: "integer", nullable: false),
                    Darkness = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelTemplates", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LabelElements",
                schema: "labels",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    LabelTemplateId = table.Column<Guid>(type: "uuid", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    X = table.Column<int>(type: "integer", nullable: false),
                    Y = table.Column<int>(type: "integer", nullable: false),
                    Rotation = table.Column<string>(type: "character varying(1)", maxLength: 1, nullable: false),
                    FieldCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Literal = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    FontHeight = table.Column<int>(type: "integer", nullable: true),
                    FontWidth = table.Column<int>(type: "integer", nullable: true),
                    Symbology = table.Column<int>(type: "integer", nullable: true),
                    BarcodeHeight = table.Column<int>(type: "integer", nullable: true),
                    ShowInterpretation = table.Column<bool>(type: "boolean", nullable: false),
                    WidthDots = table.Column<int>(type: "integer", nullable: true),
                    HeightDots = table.Column<int>(type: "integer", nullable: true),
                    ThicknessDots = table.Column<int>(type: "integer", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LabelElements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LabelElements_LabelTemplates_LabelTemplateId",
                        column: x => x.LabelTemplateId,
                        principalSchema: "labels",
                        principalTable: "LabelTemplates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LabelElements_LabelTemplateId_Order",
                schema: "labels",
                table: "LabelElements",
                columns: new[] { "LabelTemplateId", "Order" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrints_IdempotencyKey",
                schema: "labels",
                table: "LabelPrints",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrints_LabelTemplateId",
                schema: "labels",
                table: "LabelPrints",
                column: "LabelTemplateId");

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrints_SubjectKey_PrintedAt",
                schema: "labels",
                table: "LabelPrints",
                columns: new[] { "SubjectKey", "PrintedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelPrints_WarehouseId_PrintedAt",
                schema: "labels",
                table: "LabelPrints",
                columns: new[] { "WarehouseId", "PrintedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_Kind",
                schema: "labels",
                table: "LabelTemplates",
                column: "Kind",
                unique: true,
                filter: "\"Status\" = 2 AND \"WarehouseId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_Kind_Status",
                schema: "labels",
                table: "LabelTemplates",
                columns: new[] { "Kind", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_LabelTemplates_Kind_WarehouseId",
                schema: "labels",
                table: "LabelTemplates",
                columns: new[] { "Kind", "WarehouseId" },
                unique: true,
                filter: "\"Status\" = 2 AND \"WarehouseId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LabelElements",
                schema: "labels");

            migrationBuilder.DropTable(
                name: "LabelPrints",
                schema: "labels");

            migrationBuilder.DropTable(
                name: "LabelTemplates",
                schema: "labels");
        }
    }
}
