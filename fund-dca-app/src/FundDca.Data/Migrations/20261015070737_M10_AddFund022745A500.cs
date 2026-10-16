using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M10_AddFund022745A500 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 新跟踪指数：中证A500（000510），PE-TTM 口径，默认 30/70 百分位阈值。
            // 蛋卷未覆盖该指数，估值采集自动走第②优先级——中证官网自有 PE 序列现算百分位
            //（指数 2024-09 发布，样本不足 10 年时按实际样本计算）。
            migrationBuilder.Sql("""
                INSERT INTO indexes (code, name, metric, low_threshold_percent, high_threshold_percent, window_years, proxy_code)
                VALUES ('000510', '中证A500', 'PeTtm', 30, 70, 10, NULL)
                ON CONFLICT (code) DO NOTHING;
                """);

            // 新基金档案：博道中证A500指数增强A（022745），2026-10-15 首笔买入。
            // 权益型，归入「成长」赛道（中证A500 为成长型策略定位，用户 2026-10-15 确认）；
            // 申购费率 0.12%：份额按"净申购金额 = 金额 / (1 + 费率)"折算（TradeMath 外扣法），
            // 账户本金记实付金额，买入后市值小于本金、差额即申购费。
            migrationBuilder.Sql("""
                INSERT INTO funds (code, name, type, tracked_index_code, sector_id, is_active,
                                   buy_fee_rate, redeem_fee_tiers, dividend_method, use_manual_value)
                VALUES ('022745', '博道中证A500指数增强A', 'Stock', '000510', 5, true,
                        0.0012, '[]', 'REINVEST', false)
                ON CONFLICT (code) DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM funds WHERE code = '022745';");
            migrationBuilder.Sql("DELETE FROM indexes WHERE code = '000510';");
        }
    }
}
