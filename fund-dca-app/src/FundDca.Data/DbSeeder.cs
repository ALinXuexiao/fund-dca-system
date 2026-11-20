using FundDca.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace FundDca.Data;

/// <summary>
/// 种子数据（真实持仓）：
/// 份额/净值来源 2026-09-11 蚂蚁基金资产证明（净值日期 2026-09-10）；
/// 累计本金来源支付宝"全部持有"截图（持有收益反推：本金=市值-持有收益）。
/// "7日理财+"为证明中 9 只带括号子产品的合并标的，按货币基金手工维护市值，不拆分、不采集净值。
/// 恒生消费指数蛋卷未覆盖，估值按基金管理人公开披露（PE≈15、近5年约5%分位）手工播种，Source=MANUAL。
/// </summary>
public static class DbSeeder
{
    private static readonly DateOnly ProofDate = new(2026, 9, 10);

    public static async Task SeedAsync(FundDcaDbContext db, CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        // 全新库判定必须在目录播种之前：目录每次启动都会补数据，不能再用"有没有基金"当个人数据的闸门
        var isNewDatabase = !await db.Funds.AnyAsync(ct);

        // 参考数据（赛道/指数/基金档案）每次启动幂等对齐
        await FundCatalogSeeder.SeedAsync(db);
        await EnsureDcaSettingAsync(db);

        if (!isNewDatabase)
        {
            return;
        }

        // ---------- 当前持仓（份额来自资产证明；成本=支付宝截图"市值-持有收益"反推） ----------
        var now = DateTimeOffset.UtcNow;
        var holdings = new List<Holding>
        {
            // code, 份额, 累计投入本金（按支付宝截图收益率反推，保证总收益率与截图一致）
            H("024393", 792.63m, 695.20m),   // 恒生消费 -8.81%
            H("022849", 217.66m, 250.00m),   // A50 +3.13%
            H("020602", 514.46m, 543.47m),   // 红利低波 +3.91%
            H("501060", 65.54m, 150.00m),    // 优选300 +6.17%
            H("028272", 200.87m, 199.86m),   // REITs -0.29%
            H("160218", 183.53m, 100.00m),   // 房地产 +6.74%
            H("004672", 655.74m, 738.49m),   // 华夏短债 +0.08%
            H("008975", 258.76m, 299.57m),   // 消费50 -0.95%
            H("015282", 178.05m, 197.19m),   // 恒生科技 -5.37%
            H("001550", 265.35m, 200.00m),   // 医药100 -2.23%
        };
        holdings.ForEach(h =>
        {
            h.OpenedAt = ProofDate;
            h.UpdatedAt = now;
        });
        await db.Holdings.AddRangeAsync(holdings);

        // ---------- 净值（由资产证明"资产小计/总份额"反推至 6 位小数，保证 D 与证明 6383.64 一致） ----------
        var proofNavs = new List<FundNav>
        {
            Nav("024393", 0.799806m),
            Nav("022849", 1.184508m),
            Nav("020602", 1.097695m),
            Nav("501060", 2.429966m),
            Nav("028272", 0.992084m),
            Nav("160218", 0.581594m),
            Nav("004672", 1.127093m),
            Nav("008975", 1.146700m),
            Nav("015282", 1.048020m),
            Nav("001550", 0.736914m),
        };
        proofNavs.ForEach(n =>
        {
            n.Source = "PDF_PROOF";
            n.FetchedAt = now;
        });
        await db.FundNavs.AddRangeAsync(proofNavs);

        // ---------- 手工市值（兴银货币 + 7日理财+；按支付宝最新截图） ----------
        await db.ManualValues.AddRangeAsync(
            new ManualValue { FundCode = "018092", MarketValue = 2030.41m, ValueDate = ProofDate, UpdatedAt = now },
            new ManualValue { FundCode = "7R0001", MarketValue = 1013.13m, ValueDate = ProofDate, UpdatedAt = now });

        // ---------- 手工指数估值（蛋卷未覆盖：恒生消费 PE≈15、近5年约5%分位，来自基金管理人公开披露 2026-08） ----------
        // M2 迁移对全新库也会幂等补入该行，这里跳过避免主键冲突
        if (!await db.IndexValuations.AnyAsync(v => v.IndexCode == "HSCGSI" && v.TradeDate == ProofDate))
        {
            await db.IndexValuations.AddAsync(new IndexValuation
            {
                IndexCode = "HSCGSI",
                TradeDate = ProofDate,
                PeTtm = 15m,
                PePercentile = 5m,
                Pb = null,
                PbPercentile = null,
                ResolvedCode = null,
                Source = "MANUAL",
                FetchedAt = now,
            });
        }

        // ---------- 当月预算资金池（银行卡，不计入 D；默认演示值，页面可改） ----------
        await db.BudgetMonths.AddAsync(new BudgetMonth
        {
            YearMonth = "2026-09",
            BudgetAmount = 2000m,
            InvestedAmount = 0m,
        });

        await db.SaveChangesAsync();
    }

    /// <summary>定投全局设置单行（Id=1，默认 50 元）：缺则补，已有不覆盖用户改过的金额。</summary>
    private static async Task EnsureDcaSettingAsync(FundDcaDbContext db)
    {
        if (await db.DcaSettings.AnyAsync(s => s.Id == 1))
        {
            return;
        }
        db.DcaSettings.Add(new DcaSetting
        {
            Id = 1,
            FixedInvestAmount = 50m,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    private static Holding H(string code, decimal shares, decimal cost) =>
        new() { FundCode = code, Shares = shares, CostAmount = cost };

    private static FundNav Nav(string code, decimal unitNav) =>
        new() { FundCode = code, TradeDate = ProofDate, UnitNav = unitNav };
}
