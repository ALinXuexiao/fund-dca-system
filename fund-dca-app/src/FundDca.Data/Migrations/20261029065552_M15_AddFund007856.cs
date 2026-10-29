using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M15_AddFund007856 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 新跟踪指数：中证800（000906），PE-TTM 口径，默认 30/70 百分位阈值。
            // 蛋卷外部码按既有映射为 SH000906，中证官网 PE 接口直接用 6 位代码，东财盘中行情 secid=1.000906。
            migrationBuilder.Sql("""
                INSERT INTO indexes (code, name, metric, low_threshold_percent, high_threshold_percent, window_years, proxy_code)
                VALUES ('000906', '中证800', 'PeTtm', 30, 70, 10, NULL)
                ON CONFLICT (code) DO NOTHING;
                """);

            // 新基金档案：易方达中证800ETF联接A（007856），2026-10-29 首笔买入。
            // 权益型，归入「大盘」赛道（用户 2026-10-29 确认）；
            // 申购费率 0.06%：份额按"净申购金额 = 金额 / (1 + 费率)"折算（TradeMath 外扣法），
            // 账户本金记实付金额，买入后市值小于本金、差额即申购费。
            migrationBuilder.Sql("""
                INSERT INTO funds (code, name, type, tracked_index_code, sector_id, is_active,
                                   buy_fee_rate, redeem_fee_tiers, dividend_method, use_manual_value)
                VALUES ('007856', '易方达中证800ETF联接A', 'Stock', '000906', 1, true,
                        0.0006, '[]', 'REINVEST', false)
                ON CONFLICT (code) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM funds WHERE code = '007856';");
            migrationBuilder.Sql("DELETE FROM indexes WHERE code = '000906';");
        }
    }
}
