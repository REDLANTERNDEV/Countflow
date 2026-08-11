using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Countflow.Modules.Inventory.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialInventory : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "inventory");

            migrationBuilder.CreateTable(
                name: "LicensePlates",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sscc = table.Column<string>(type: "character(18)", fixedLength: true, maxLength: 18, nullable: false),
                    ParentLicensePlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsClosed = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LicensePlates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_LicensePlates_LicensePlates_ParentLicensePlateId",
                        column: x => x.ParentLicensePlateId,
                        principalSchema: "inventory",
                        principalTable: "LicensePlates",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Lots",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    ProductionDate = table.Column<DateOnly>(type: "date", nullable: true),
                    SupplierLotNumber = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Lots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StockBalances",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    LocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    LicensePlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    AllowsNegative = table.Column<bool>(type: "boolean", nullable: false),
                    LastMovementSequence = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockBalances", x => x.Id);
                    table.CheckConstraint("CK_StockBalances_NonNegativeUnlessCounterparty", "\"Quantity\" >= 0 OR \"AllowsNegative\"");
                });

            migrationBuilder.CreateTable(
                name: "StockMovements",
                schema: "inventory",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Sequence = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityAlwaysColumn),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    LotId = table.Column<Guid>(type: "uuid", nullable: true),
                    Quantity = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    FromLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToLocationId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromLicensePlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToLicensePlateId = table.Column<Guid>(type: "uuid", nullable: true),
                    FromStatus = table.Column<int>(type: "integer", nullable: false),
                    ToStatus = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<int>(type: "integer", nullable: false),
                    Reason = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    DecisionTraceId = table.Column<Guid>(type: "uuid", nullable: true),
                    ReversesMovementId = table.Column<Guid>(type: "uuid", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Notes = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StockMovements", x => x.Id);
                    table.CheckConstraint("CK_StockMovements_DistinctHoldings", "NOT (\"FromLocationId\" = \"ToLocationId\"\n     AND \"FromLicensePlateId\" IS NOT DISTINCT FROM \"ToLicensePlateId\"\n     AND \"FromStatus\" = \"ToStatus\")");
                    table.CheckConstraint("CK_StockMovements_PositiveQuantity", "\"Quantity\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_LicensePlates_ParentLicensePlateId",
                schema: "inventory",
                table: "LicensePlates",
                column: "ParentLicensePlateId");

            migrationBuilder.CreateIndex(
                name: "IX_LicensePlates_Sscc",
                schema: "inventory",
                table: "LicensePlates",
                column: "Sscc",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Lots_ExpiryDate",
                schema: "inventory",
                table: "Lots",
                column: "ExpiryDate");

            migrationBuilder.CreateIndex(
                name: "IX_Lots_ItemId_LotNumber",
                schema: "inventory",
                table: "Lots",
                columns: new[] { "ItemId", "LotNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_ItemId_LotId_LocationId_LicensePlateId_Status",
                schema: "inventory",
                table: "StockBalances",
                columns: new[] { "ItemId", "LotId", "LocationId", "LicensePlateId", "Status" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_ItemId_Status",
                schema: "inventory",
                table: "StockBalances",
                columns: new[] { "ItemId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_LicensePlateId",
                schema: "inventory",
                table: "StockBalances",
                column: "LicensePlateId");

            migrationBuilder.CreateIndex(
                name: "IX_StockBalances_LocationId_ItemId",
                schema: "inventory",
                table: "StockBalances",
                columns: new[] { "LocationId", "ItemId" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_FromLocationId_Sequence",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "FromLocationId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_IdempotencyKey",
                schema: "inventory",
                table: "StockMovements",
                column: "IdempotencyKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ItemId_Sequence",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "ItemId", "Sequence" });

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_LotId",
                schema: "inventory",
                table: "StockMovements",
                column: "LotId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_OccurredAt",
                schema: "inventory",
                table: "StockMovements",
                column: "OccurredAt");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ReversesMovementId",
                schema: "inventory",
                table: "StockMovements",
                column: "ReversesMovementId",
                unique: true,
                filter: "\"ReversesMovementId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_Sequence",
                schema: "inventory",
                table: "StockMovements",
                column: "Sequence",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_TaskId",
                schema: "inventory",
                table: "StockMovements",
                column: "TaskId");

            migrationBuilder.CreateIndex(
                name: "IX_StockMovements_ToLocationId_Sequence",
                schema: "inventory",
                table: "StockMovements",
                columns: new[] { "ToLocationId", "Sequence" });

            // The last and strongest of the three guards on the append-only
            // invariant (ADR 0001). The entity is init-only and the DbContext
            // refuses to persist a modified entry — but both of those live in one
            // application, and the ledger is also reachable from psql, from a
            // migration, from a reporting tool, and from whatever gets written
            // against this schema in five years. Only the database can say no to
            // all of them.
            //
            // FOR EACH STATEMENT rather than FOR EACH ROW: a statement-level
            // trigger rejects `UPDATE ... WHERE false` too. That matters, because
            // a zero-row UPDATE that "succeeds" is how someone concludes the table
            // is editable and writes the code that expects it to be.
            //
            // TRUNCATE is included: it is not a DELETE and would otherwise walk
            // straight past a DELETE-only guard.
            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION inventory.reject_stock_ledger_mutation()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $$
                BEGIN
                    RAISE EXCEPTION
                        'inventory."StockMovements" is append-only; % is not permitted. Post a compensating entry instead.',
                        TG_OP
                        USING ERRCODE = 'restrict_violation';
                END;
                $$;
                """);

            migrationBuilder.Sql(
                """
                CREATE TRIGGER stock_movements_append_only
                BEFORE UPDATE OR DELETE OR TRUNCATE ON inventory."StockMovements"
                FOR EACH STATEMENT
                EXECUTE FUNCTION inventory.reject_stock_ledger_mutation();
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The trigger goes with its table, but the function does not — it is a
            // schema-level object and would survive a rollback, so a later re-apply
            // would meet it still there. CREATE OR REPLACE above tolerates that;
            // dropping it here keeps Down genuinely reversible.
            migrationBuilder.Sql(
                """DROP TRIGGER IF EXISTS stock_movements_append_only ON inventory."StockMovements";""");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS inventory.reject_stock_ledger_mutation();");

            migrationBuilder.DropTable(
                name: "LicensePlates",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "Lots",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockBalances",
                schema: "inventory");

            migrationBuilder.DropTable(
                name: "StockMovements",
                schema: "inventory");
        }
    }
}
