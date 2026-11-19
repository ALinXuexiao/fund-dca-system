using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M16_AddFunds021172_016630 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 新跟踪指数：北证50（899050）、中证1000（000852），均按 PE-TTM 口径、默认 30/70 百分位阈值。
            // 东财盘中行情 secid 分别为 0.899050 / 1.000852。
            migrationBuilder.Sql("""
                INSERT INTO indexes (code, name, metric, low_threshold_percent, high_threshold_percent, window_years, proxy_code)
                VALUES ('899050', '北证50', 'PeTtm', 30, 70, 10, NULL),
                       ('000852', '中证1000', 'PeTtm', 30, 70, 10, NULL)
                ON CONFLICT (code) DO NOTHING;
                """);

            // 新基金档案（2026-11-19 首笔买入）：
            //   021172 华安北证50成份指数A —— 申购费率 0.06%，归「小盘」赛道
            //   016630 易方达中证1000ETF联接A —— 申购费率 0.12%，归「小盘」赛道
            // 份额按"净申购金额 = 金额 / (1 + 费率)"外扣折算（TradeMath），账户本金记实付金额。
            migrationBuilder.Sql("""
                INSERT INTO funds (code, name, type, tracked_index_code, sector_id, is_active,
                                   buy_fee_rate, redeem_fee_tiers, dividend_method, use_manual_value)
                VALUES ('021172', '华安北证50成份指数A', 'Stock', '899050', 3, true,
                        0.0006, '[]', 'REINVEST', false),
                       ('016630', '易方达中证1000ETF联接A', 'Stock', '000852', 3, true,
                        0.0012, '[]', 'REINVEST', false)
                ON CONFLICT (code) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM funds WHERE code IN ('021172', '016630');");
            migrationBuilder.Sql("DELETE FROM indexes WHERE code IN ('899050', '000852');");
        }
    }
}
