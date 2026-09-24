using FundDca.Core.Domain;

namespace FundDca.Core.Rules;

/// <summary>对账差异类型（M3：先对账、后覆盖，差异不静默处理）。</summary>
public enum ReconcileKind
{
    /// <summary>份额/市值与系统一致。</summary>
    Matched,

    /// <summary>文件份额多于系统（无现金流）：默认红利再投，只加份额不加本金。</summary>
    ShareIncrease,

    /// <summary>文件份额少于系统：M3 不处理卖出，默认保留系统值，不静默减仓。</summary>
    ShareDecrease,

    /// <summary>文件有、系统无：需建档并归类。</summary>
    NewFund,

    /// <summary>系统有、文件无：默认保留系统值（证明可能是筛选导出，不删持仓）。</summary>
    MissingInFile,

    /// <summary>手工市值标的（货币/理财）：文件市值与系统不同。</summary>
    ManualValueChange,

    /// <summary>无法识别代码或缺少关键字段的文件行。</summary>
    UnknownRow,
}

/// <summary>用户对一条差异可选择的处理动作。</summary>
public enum ReconcileAction
{
    /// <summary>一致，无需处理。</summary>
    None,

    /// <summary>红利再投：份额 += 差额，本金不变。</summary>
    DividendReinvest,

    /// <summary>手动加仓：份额 += 差额，本金 += 估算成本（差额×最新净值，可改）。</summary>
    ManualAdd,

    /// <summary>保留系统值，忽略本行文件值。</summary>
    KeepSystem,

    /// <summary>接受文件市值（手工市值标的）。</summary>
    AcceptFileValue,

    /// <summary>新基金建档并建仓。</summary>
    CreateFund,
}

/// <summary>解析器产出的一行持仓（文件口径）。</summary>
public record ParsedPosition(string? Code, string Name, decimal? Shares, decimal? MarketValue)
{
    public string? RawLine { get; init; }
}

/// <summary>一份资产证明的解析结果。</summary>
public record ParsedStatement(
    string Source,
    string? FileName,
    string TemplateVersion,
    DateOnly? ProofDate,
    IReadOnlyList<ParsedPosition> Positions,
    IReadOnlyList<string> Warnings);

/// <summary>系统当前持仓口径（对账右侧）。</summary>
public record SystemPosition(
    string Code,
    string Name,
    FundType Type,
    int? SectorId,
    string? SectorName,
    bool IsManual,
    decimal Shares,
    decimal? CostAmount,
    decimal MarketValue,
    decimal? UnitNav);

/// <summary>一条对账项（一个基金一行）。</summary>
public record ReconcileItem(
    string? Code,
    string FileName,
    decimal? FileShares,
    decimal? FileMarketValue,
    bool ExistsInSystem,
    string? SystemName,
    FundType? SystemType,
    int? SectorId,
    string? SectorName,
    bool IsManual,
    decimal? SystemShares,
    decimal? SystemCostAmount,
    decimal? SystemMarketValue,
    decimal? UnitNav,
    ReconcileKind Kind,
    ReconcileAction Action,
    /// <summary>份额变动（文件-系统）；新基金即文件份额；红利再投/手动加仓据此入账。</summary>
    decimal SharesDelta,
    /// <summary>手动加仓默认增加本金 = 份额差 × 最新净值（用户可在提交前修改）。</summary>
    decimal? SuggestedAddedCost,
    /// <summary>新基金建议类型（用户可在前端改判）。</summary>
    FundType? ProposedType,
    IReadOnlyList<ReconcileAction> AllowedActions,
    string Message);

/// <summary>
/// 导入对账纯规则：文件持仓 × 系统持仓 → 逐项差异与默认处理动作。
/// 全部口径无副作用、可单测；落库由 ImportService 负责。
/// </summary>
public static class ImportReconciler
{
    /// <summary>份额一致容差（支付宝份额两位小数）。</summary>
    public const decimal ShareTolerance = 0.005m;

    /// <summary>市值一致容差（到分）。</summary>
    public const decimal ValueTolerance = 0.01m;

    public static List<ReconcileItem> Build(
        IReadOnlyList<ParsedPosition> parsed,
        IReadOnlyList<SystemPosition> system,
        int? bondSectorId,
        int? moneySectorId)
    {
        var items = new List<ReconcileItem>();
        var sysByCode = system.ToDictionary(s => s.Code);
        var seenCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var p in parsed)
        {
            var code = p.Code is { } c && !string.IsNullOrWhiteSpace(c) ? c.Trim() : null;

            if (code is null)
            {
                items.Add(new ReconcileItem(
                    null, p.Name, p.Shares, p.MarketValue, false, null, null, null, null, false,
                    null, null, null, null, ReconcileKind.UnknownRow, ReconcileAction.KeepSystem,
                    0, null, null, [ReconcileAction.KeepSystem],
                    "无法识别基金代码，已忽略；可改用标准 Excel 模板导入"));
                continue;
            }

            seenCodes.Add(code);

            if (!sysByCode.TryGetValue(code, out var s))
            {
                var proposed = InferType(p.Name);
                var isManual = proposed == FundType.Money;
                var sector = proposed switch
                {
                    FundType.Bond => bondSectorId,
                    FundType.Money => moneySectorId,
                    _ => (int?)null, // 权益新基金必须由用户选择赛道
                };
                var msg = proposed switch
                {
                    FundType.Money => "新货币/理财标的：建档后按手工市值维护，计入 D、不采集净值",
                    FundType.Bond => "新债券基金：归入稳健类，计入 D、不定投、正常采集净值",
                    _ => "新权益基金：请选择所属赛道后建仓，将参与 B1-B4 定投判定",
                };
                items.Add(new ReconcileItem(
                    code, p.Name, p.Shares, p.MarketValue, false, null, null, sector, null, isManual,
                    null, null, null, null, ReconcileKind.NewFund, ReconcileAction.CreateFund,
                    p.Shares ?? 0m, null, proposed, [ReconcileAction.CreateFund, ReconcileAction.KeepSystem], msg));
                continue;
            }

            if (s.IsManual)
            {
                // 货币/理财：文件口径是市值（极少数证明用"份额"列承载市值，做兜底）
                var fileValue = p.MarketValue ?? p.Shares;
                var changed = fileValue is { } fv0 && Math.Abs(fv0 - s.MarketValue) > ValueTolerance;
                items.Add(new ReconcileItem(
                    code, s.Name, null, fileValue, true, s.Name, s.Type, s.SectorId, s.SectorName, true,
                    s.Shares, s.CostAmount, s.MarketValue, s.UnitNav,
                    changed ? ReconcileKind.ManualValueChange : ReconcileKind.Matched,
                    changed ? ReconcileAction.AcceptFileValue : ReconcileAction.None,
                    0, null, null,
                    changed ? [ReconcileAction.AcceptFileValue, ReconcileAction.KeepSystem] : [ReconcileAction.None],
                    changed ? $"手工市值 {s.MarketValue:0.00} → {fileValue:0.00}" : "市值一致"));
                continue;
            }

            // 净值型基金：按份额对账
            if (p.Shares is not { } fileShares)
            {
                items.Add(new ReconcileItem(
                    code, s.Name, null, p.MarketValue, true, s.Name, s.Type, s.SectorId, s.SectorName, false,
                    s.Shares, s.CostAmount, s.MarketValue, s.UnitNav, ReconcileKind.UnknownRow,
                    ReconcileAction.KeepSystem, 0, null, null, [ReconcileAction.KeepSystem],
                    "文件行缺少份额数据，已保留系统值"));
                continue;
            }

            var delta = Math.Round(fileShares - s.Shares, 4);
            if (Math.Abs(delta) <= ShareTolerance)
            {
                items.Add(new ReconcileItem(
                    code, s.Name, fileShares, p.MarketValue, true, s.Name, s.Type, s.SectorId, s.SectorName, false,
                    s.Shares, s.CostAmount, s.MarketValue, s.UnitNav, ReconcileKind.Matched, ReconcileAction.None,
                    0, null, null, [ReconcileAction.None], "份额一致"));
            }
            else if (delta > 0)
            {
                var addedCost = s.UnitNav is { } nav && nav > 0m
                    ? Math.Round(delta * nav, 2)
                    : (decimal?)null;
                items.Add(new ReconcileItem(
                    code, s.Name, fileShares, p.MarketValue, true, s.Name, s.Type, s.SectorId, s.SectorName, false,
                    s.Shares, s.CostAmount, s.MarketValue, s.UnitNav, ReconcileKind.ShareIncrease,
                    ReconcileAction.DividendReinvest, delta, addedCost, null,
                    [ReconcileAction.DividendReinvest, ReconcileAction.ManualAdd, ReconcileAction.KeepSystem],
                    $"份额增加 {delta:0.##}：全组合默认红利再投资，按分红再投入账（不加本金）；如属手动加仓请改判"));
            }
            else
            {
                items.Add(new ReconcileItem(
                    code, s.Name, fileShares, p.MarketValue, true, s.Name, s.Type, s.SectorId, s.SectorName, false,
                    s.Shares, s.CostAmount, s.MarketValue, s.UnitNav, ReconcileKind.ShareDecrease,
                    ReconcileAction.KeepSystem, delta, null, null, [ReconcileAction.KeepSystem],
                    $"文件份额比系统少 {-delta:0.##}，M3 不处理卖出，已保留系统值；请在卖出闭环中处理"));
            }
        }

        // 系统有、文件无
        foreach (var s in system)
        {
            if (!seenCodes.Contains(s.Code))
            {
                items.Add(new ReconcileItem(
                    s.Code, s.Name, null, null, true, s.Name, s.Type, s.SectorId, s.SectorName, s.IsManual,
                    s.Shares, s.CostAmount, s.MarketValue, s.UnitNav, ReconcileKind.MissingInFile,
                    ReconcileAction.KeepSystem, 0, null, null, [ReconcileAction.KeepSystem],
                    "资产证明中缺少该标的，已保留系统持仓（不删除）"));
            }
        }

        // 差异在前、一致在后，组内按代码排序，便于用户逐项确认
        return items
            .OrderBy(i => i.Kind == ReconcileKind.Matched ? 1 : 0)
            .ThenBy(i => i.Code)
            .ToList();
    }

    /// <summary>按基金名称推断类型（新基金默认归类；用户可改判）。</summary>
    public static FundType InferType(string? name)
    {
        var n = name ?? string.Empty;
        if (n.Contains("货币") || n.Contains("现金") || n.Contains("理财") || n.Contains("余额") || n.Contains("添利"))
        {
            return FundType.Money;
        }
        if (n.Contains("债"))
        {
            return FundType.Bond;
        }
        if (n.Contains("QDII") || n.Contains("恒生") || n.Contains("纳斯达克") || n.Contains("标普")
            || n.Contains("海外") || n.Contains("全球") || n.Contains("美国") || n.Contains("日本"))
        {
            return FundType.Qdii;
        }
        return FundType.Stock;
    }
}
