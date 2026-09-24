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

    public static async Task SeedAsync(FundDcaDbContext db)
    {
        await db.Database.MigrateAsync();

        if (await db.Funds.AnyAsync())
        {
            return;
        }

        // ---------- 赛道 ----------
        var sectors = new List<Sector>
        {
            new() { Id = 1, Name = "大盘", LimitPercent = 15m, IsEquity = true, SortOrder = 10 },
            new() { Id = 2, Name = "中盘", LimitPercent = 15m, IsEquity = true, SortOrder = 20 },
            new() { Id = 3, Name = "小盘", LimitPercent = 15m, IsEquity = true, SortOrder = 30 },
            new() { Id = 4, Name = "价值", LimitPercent = 15m, IsEquity = true, SortOrder = 40 },
            new() { Id = 5, Name = "成长", LimitPercent = 15m, IsEquity = true, SortOrder = 50 },
            new() { Id = 6, Name = "红利", LimitPercent = 15m, IsEquity = true, SortOrder = 60 },
            new() { Id = 7, Name = "消费", LimitPercent = 15m, IsEquity = true, SortOrder = 70 },
            new() { Id = 8, Name = "港股科技", LimitPercent = 15m, IsEquity = true, SortOrder = 80 },
            new() { Id = 11, Name = "医药", LimitPercent = 15m, IsEquity = true, SortOrder = 84 },
            new() { Id = 12, Name = "地产", LimitPercent = 15m, IsEquity = true, SortOrder = 86 },
            new() { Id = 13, Name = "REITs", LimitPercent = 15m, IsEquity = true, SortOrder = 88 },
            new() { Id = 9, Name = "债券", LimitPercent = 100m, IsEquity = false, SortOrder = 90 },
            new() { Id = 10, Name = "货币", LimitPercent = 100m, IsEquity = false, SortOrder = 100 },
        };
        await db.Sectors.AddRangeAsync(sectors);

        // ---------- 指数（蛋卷未覆盖的内部走代理或手工估值；代理不对前端展示） ----------
        var indexes = new List<IndexInfo>
        {
            new() { Code = "930050", Name = "中证A50", Metric = ValuationMetric.PeTtm, ProxyCode = "000300" },
            new() { Code = "H30269", Name = "中证红利低波动", Metric = ValuationMetric.EarningsYield, LowThresholdPercent = 10m, HighThresholdPercent = 6.4m },
            new() { Code = "931069", Name = "中金300", Metric = ValuationMetric.PeTtm },
            new() { Code = "932047", Name = "中证REITs全收益", Metric = ValuationMetric.None },
            new() { Code = "399393", Name = "国证房地产", Metric = ValuationMetric.Pb },
            new() { Code = "931139", Name = "中证消费50", Metric = ValuationMetric.PeTtm, ProxyCode = "000932" },
            new() { Code = "HSTECH", Name = "恒生科技", Metric = ValuationMetric.PeTtm },
            new() { Code = "HSCGSI", Name = "恒生消费", Metric = ValuationMetric.PeTtm },
            new() { Code = "000978", Name = "中证医药100", Metric = ValuationMetric.PeTtm },
            // 代理目标指数
            new() { Code = "000300", Name = "沪深300", Metric = ValuationMetric.PeTtm },
            new() { Code = "000932", Name = "中证主要消费", Metric = ValuationMetric.PeTtm },
        };
        // M2 迁移会以 ON CONFLICT DO NOTHING 幂等补入 HSCGSI / 932047（老库修正）；全新库播种时跳过已存在的指数
        var existingIndexCodes = await db.Indexes.Select(i => i.Code).ToListAsync();
        await db.Indexes.AddRangeAsync(indexes.Where(i => !existingIndexCodes.Contains(i.Code)));

        // ---------- 基金档案（12 个标的；7日理财+ 为 9 只子产品合并，按货币基金手工维护） ----------
        var funds = new List<Fund>
        {
            New("024393", "永赢恒生消费指数(QDII)A", FundType.Qdii, 7, "HSCGSI"),
            New("022849", "招商中证A50指数增强A", FundType.Stock, 5, "930050"),
            New("020602", "易方达红利低波ETF联接A", FundType.Stock, 6, "H30269"),
            New("501060", "中金中证优选300指数(LOF)A", FundType.Stock, 4, "931069"),
            New("028272", "中金中证REITs全收益指数(FOF)A", FundType.Mixed, 13, "932047"),
            New("160218", "国泰国证房地产行业指数(LOF)A", FundType.Stock, 12, "399393"),
            New("004672", "华夏短债债券A", FundType.Bond, 9, null),
            New("008975", "富国中证消费50ETF联接A", FundType.Stock, 7, "931139"),
            New("015282", "华安恒生科技ETF联接(QDII)A", FundType.Qdii, 8, "HSTECH"),
            New("001550", "天弘中证医药100指数A", FundType.Stock, 11, "000978"),
            New("018092", "兴银现金添利货币C", FundType.Money, 10, null, manual: true),
            New("7R0001", "7日理财+", FundType.Money, 10, null, manual: true),
        };
        await db.Funds.AddRangeAsync(funds);

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

        // 注：定投全局设置单行（Id=1，固定 50 元）由 M2 迁移以 ON CONFLICT DO NOTHING 幂等写入，此处不再重复种子

        // ---------- 当月预算资金池（银行卡，不计入 D；默认演示值，页面可改） ----------
        await db.BudgetMonths.AddAsync(new BudgetMonth
        {
            YearMonth = "2026-09",
            BudgetAmount = 2000m,
            InvestedAmount = 0m,
        });

        await db.SaveChangesAsync();
    }

    private static Fund New(string code, string name, FundType type, int sectorId, string? indexCode, bool manual = false) =>
        new()
        {
            Code = code,
            Name = name,
            Type = type,
            SectorId = sectorId,
            TrackedIndexCode = indexCode,
            UseManualValue = manual,
            DividendMethod = "REINVEST",
        };

    private static Holding H(string code, decimal shares, decimal cost) =>
        new() { FundCode = code, Shares = shares, CostAmount = cost };

    private static FundNav Nav(string code, decimal unitNav) =>
        new() { FundCode = code, TradeDate = ProofDate, UnitNav = unitNav };
}
