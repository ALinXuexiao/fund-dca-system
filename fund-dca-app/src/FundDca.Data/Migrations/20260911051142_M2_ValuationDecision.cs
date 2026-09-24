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

            // ---------- M2 数据修正（幂等；对老库补数据，对全新库为 no-op） ----------

            // 指数：恒生消费（手工估值）、中证REITs全收益真实代码 932047、代理配置
            migrationBuilder.Sql("""
                INSERT INTO indexes (code, name, metric, low_threshold_percent, high_threshold_percent, window_years, proxy_code)
                VALUES ('HSCGSI', '恒生消费', 'PeTtm', 30, 70, 10, NULL)
                ON CONFLICT (code) DO NOTHING;
                """);
            migrationBuilder.Sql("""
                INSERT INTO indexes (code, name, metric, low_threshold_percent, high_threshold_percent, window_years, proxy_code)
                VALUES ('932047', '中证REITs全收益', 'None', 30, 70, 10, NULL)
                ON CONFLICT (code) DO NOTHING;
                """);
            migrationBuilder.Sql("UPDATE indexes SET proxy_code = '000300' WHERE code = '930050';");
            migrationBuilder.Sql("UPDATE indexes SET proxy_code = '000991' WHERE code = '931769';");
            migrationBuilder.Sql("UPDATE indexes SET proxy_code = '000932' WHERE code = '931139';");

            // 老库占位代码 CSIREIT → 932047，随后删除占位行
            migrationBuilder.Sql("UPDATE funds SET tracked_index_code = '932047' WHERE tracked_index_code = 'CSIREIT';");
            migrationBuilder.Sql("DELETE FROM indexes WHERE code = 'CSIREIT';");

            // 基金：恒生消费挂接指数；A50 归入成长赛道；7日理财+ 改货币基金/货币赛道/去掉括号
            migrationBuilder.Sql("UPDATE funds SET tracked_index_code = 'HSCGSI' WHERE code = '024393';");
            migrationBuilder.Sql("UPDATE funds SET sector_id = 5 WHERE code = '022849';");
            migrationBuilder.Sql("UPDATE funds SET name = '7日理财+', type = 'Money', sector_id = 10 WHERE code = '7R0001';");
            migrationBuilder.Sql("UPDATE funds SET use_manual_value = true WHERE code IN ('018092', '7R0001');");

            // 持仓累计本金（按支付宝截图收益率反推）
            migrationBuilder.Sql("""
                UPDATE holdings SET cost_amount = v.cost
                FROM (VALUES
                    ('024393', 695.20),
                    ('022849', 250.00),
                    ('020602', 543.47),
                    ('501060', 150.00),
                    ('028272', 199.86),
                    ('160218', 100.00),
                    ('004672', 738.49),
                    ('008975', 299.57),
                    ('015282', 197.19),
                    ('001550', 200.00)
                ) AS v(code, cost)
                WHERE holdings.fund_code = v.code;
                """);

            // 7日理财+ 手工市值按最新截图 1013.13
            migrationBuilder.Sql("UPDATE manual_values SET market_value = 1013.13 WHERE fund_code = '7R0001';");

            // 恒生消费手工估值（管理人披露：PE≈15、近5年约5%分位；蛋卷未覆盖，刷新时保留 MANUAL）
            migrationBuilder.Sql("""
                INSERT INTO index_valuations
                    (index_code, trade_date, pe_ttm, pe_percentile, pb, pb_percentile, resolved_code, source, fetched_at)
                VALUES ('HSCGSI', DATE '2026-09-10', 15, 5, NULL, NULL, NULL, 'MANUAL', now())
                ON CONFLICT (index_code, trade_date) DO NOTHING;
                """);

            // 定投全局设置：单次固定 50 元
            migrationBuilder.Sql("""
                INSERT INTO dca_settings (id, fixed_invest_amount, updated_at)
                VALUES (1, 50, now())
                ON CONFLICT (id) DO NOTHING;
                """);
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
