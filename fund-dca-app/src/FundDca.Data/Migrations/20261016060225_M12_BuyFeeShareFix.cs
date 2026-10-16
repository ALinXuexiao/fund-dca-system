using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M12_BuyFeeShareFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 修正历史买入流水：此前份额按"金额 / 净值"直接折算，漏扣申购费，
            // 导致买入后市值虚高（等于本金）。正确口径为净申购金额外扣：
            //     份额 = 净申购金额 / 净值 = 原份额 / (1 + 申购费率)   ← 净值上下约掉，无需回查净值。
            // 持仓按"确认份额差额"同步修正（已确认流水此前就是按确认份额入账的）；
            // 未确认（Pending）流水其持仓为预估份额预占，按预估份额差额修正。
            // 已撤销（Cancelled）流水预占与反恢相互抵消、不影响持仓，保留原值作审计。
            // 注意：先修正持仓、再改写流水份额，避免持仓用到已被改写后的值。
            migrationBuilder.Sql("""
                WITH adj AS (
                    SELECT t.fund_code,
                           SUM(
                               CASE WHEN t.confirmed_shares IS NOT NULL
                                    THEN ROUND(t.confirmed_shares / (1 + f.buy_fee_rate), 4) - t.confirmed_shares
                                    ELSE ROUND(t.estimated_shares / (1 + f.buy_fee_rate), 4) - t.estimated_shares
                               END
                           ) AS delta
                    FROM trades t
                    JOIN funds f ON f.code = t.fund_code
                    WHERE t.status <> 'Cancelled'
                      AND f.buy_fee_rate IS NOT NULL
                      AND f.buy_fee_rate > 0
                    GROUP BY t.fund_code
                )
                UPDATE holdings h
                SET shares = ROUND(h.shares + adj.delta, 4)
                FROM adj
                WHERE h.fund_code = adj.fund_code;
                """);

            migrationBuilder.Sql("""
                UPDATE trades t
                SET estimated_shares = ROUND(t.estimated_shares / (1 + f.buy_fee_rate), 4),
                    confirmed_shares = CASE
                        WHEN t.confirmed_shares IS NULL THEN NULL
                        ELSE ROUND(t.confirmed_shares / (1 + f.buy_fee_rate), 4)
                    END
                FROM funds f
                WHERE f.code = t.fund_code
                  AND t.status <> 'Cancelled'
                  AND f.buy_fee_rate IS NOT NULL
                  AND f.buy_fee_rate > 0;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // 回退：把份额按 (1 + 申购费率) 还原为"漏扣申购费"的旧口径。
            migrationBuilder.Sql("""
                WITH adj AS (
                    SELECT t.fund_code,
                           SUM(
                               CASE WHEN t.confirmed_shares IS NOT NULL
                                    THEN ROUND(t.confirmed_shares * (1 + f.buy_fee_rate), 4) - t.confirmed_shares
                                    ELSE ROUND(t.estimated_shares * (1 + f.buy_fee_rate), 4) - t.estimated_shares
                               END
                           ) AS delta
                    FROM trades t
                    JOIN funds f ON f.code = t.fund_code
                    WHERE t.status <> 'Cancelled'
                      AND f.buy_fee_rate IS NOT NULL
                      AND f.buy_fee_rate > 0
                    GROUP BY t.fund_code
                )
                UPDATE holdings h
                SET shares = ROUND(h.shares + adj.delta, 4)
                FROM adj
                WHERE h.fund_code = adj.fund_code;
                """);

            migrationBuilder.Sql("""
                UPDATE trades t
                SET estimated_shares = ROUND(t.estimated_shares * (1 + f.buy_fee_rate), 4),
                    confirmed_shares = CASE
                        WHEN t.confirmed_shares IS NULL THEN NULL
                        ELSE ROUND(t.confirmed_shares * (1 + f.buy_fee_rate), 4)
                    END
                FROM funds f
                WHERE f.code = t.fund_code
                  AND t.status <> 'Cancelled'
                  AND f.buy_fee_rate IS NOT NULL
                  AND f.buy_fee_rate > 0;
                """);
        }
    }
}
