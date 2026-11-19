using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M2_ValuationDecision : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "proxy_code",
                table: "indexes",
                type: "character varying(20)",
                maxLength: 20,
                nullable: true);

            migrationBuilder.AlterColumn<decimal>(
                name: "cost_amount",
                table: "holdings",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: true,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4);

            migrationBuilder.AddColumn<bool>(
                name: "use_manual_value",
                table: "funds",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "dca_settings",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    fixed_invest_amount = table.Column<decimal>(type: "numeric(10,2)", precision: 10, scale: 2, nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_dca_settings", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "index_valuations",
                columns: table => new
                {
                    index_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    trade_date = table.Column<DateOnly>(type: "date", nullable: false),
                    pe_ttm = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    pe_percentile = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    pb = table.Column<decimal>(type: "numeric(10,4)", precision: 10, scale: 4, nullable: true),
                    pb_percentile = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    resolved_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_index_valuations", x => new { x.index_code, x.trade_date });
                    table.ForeignKey(
                        name: "FK_index_valuations_indexes_index_code",
                        column: x => x.index_code,
                        principalTable: "indexes",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_index_valuations_trade_date",
                table: "index_valuations",
                column: "trade_date");

            // 参考数据（指数/基金/定投设置）已移到 Catalog/fund-catalog.json + FundCatalogSeeder，
            // 迁移只保留结构变更，避免历史里混入业务数据。
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "dca_settings");

            migrationBuilder.DropTable(
                name: "index_valuations");

            migrationBuilder.DropColumn(
                name: "proxy_code",
                table: "indexes");

            migrationBuilder.DropColumn(
                name: "use_manual_value",
                table: "funds");

            migrationBuilder.AlterColumn<decimal>(
                name: "cost_amount",
                table: "holdings",
                type: "numeric(18,4)",
                precision: 18,
                scale: 4,
                nullable: false,
                defaultValue: 0m,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,4)",
                oldPrecision: 18,
                oldScale: 4,
                oldNullable: true);
        }
    }
}
