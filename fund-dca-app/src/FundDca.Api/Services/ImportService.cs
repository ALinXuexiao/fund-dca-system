using FundDca.Api.Models;
using FundDca.Core.Domain;
using FundDca.Core.Rules;
using FundDca.Data;
using FundDca.Import.Parsing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace FundDca.Api.Services;

/// <summary>
/// M3 资产证明导入与对账（PRD 5.3）：
/// 解析（PDF/Excel/CSV/演示）→ 先对账后覆盖、差异不静默 → 事务应用 → 落持仓版本快照（原子切换）→ 支持一键回滚。
/// 解析预览暂存内存缓存（单用户本机，30 分钟），提交时只信任缓存中的文件值与用户的“选择”，不信任前端改写的数字。
/// </summary>
public class ImportService(
    FundDcaDbContext db,
    StatementParserRegistry parsers,
    IMemoryCache cache,
    ILogger<ImportService> logger)
{
    private const string TokenPrefix = "import-stmt:";
    private static readonly TimeSpan PreviewTtl = TimeSpan.FromMinutes(30);

    // ---------- 解析 + 预览（不落库） ----------

    public async Task<ImportPreviewDto> ParseDemoAsync(CancellationToken ct)
    {
        var statement = DemoStatementProvider.Build();
        return await BuildPreviewAsync(statement, ct);
    }

    public async Task<ImportPreviewDto> ParseUploadAsync(Stream content, string fileName, CancellationToken ct)
    {
        var parser = parsers.Resolve(fileName)
            ?? throw new InvalidOperationException($"不支持的文件类型：{fileName}（请上传 PDF / XLSX / CSV）");

        // 解析器内部会从头读取流
        if (content.CanSeek)
        {
            content.Position = 0;
        }

        var statement = await parser.ParseAsync(content, fileName, ct);
        return await BuildPreviewAsync(statement, ct);
    }

    private async Task<ImportPreviewDto> BuildPreviewAsync(ParsedStatement statement, CancellationToken ct)
    {
        var (system, bondSector, moneySector, sectors) = await LoadSystemAsync(ct);
        var items = ImportReconciler.Build(statement.Positions, system, bondSector, moneySector);

        var token = Guid.NewGuid().ToString("N");
        cache.Set(TokenPrefix + token, statement, PreviewTtl);

        var dtos = items.Select(ToDto).ToList();
        return new ImportPreviewDto(
            token,
            statement.Source,
            statement.FileName,
            statement.TemplateVersion,
            statement.ProofDate?.ToString("yyyy-MM-dd"),
            statement.Warnings,
            dtos,
            sectors,
            Summarize(items));
    }

    // ---------- 确认导入（事务 + 版本快照） ----------

    public async Task<CommitResultDto> CommitAsync(CommitRequestDto req, CancellationToken ct)
    {
        if (!cache.TryGetValue(TokenPrefix + req.Token, out ParsedStatement? statement) || statement is null)
        {
            throw new InvalidOperationException("解析结果已过期或不存在，请返回上一步重新解析文件");
        }

        var proofDate = ResolveProofDate(req.ProofDate, statement.ProofDate);
        var decisions = (req.Decisions ?? [])
            .Where(d => d.Code is not null)
            .GroupBy(d => d.Code!)
            .ToDictionary(g => g.Key, g => g.Last(), StringComparer.OrdinalIgnoreCase);

        var (system, bondSector, moneySector, _) = await LoadSystemAsync(ct);
        var items = ImportReconciler.Build(statement.Positions, system, bondSector, moneySector);

        var logs = new List<string>();
        var changeKinds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var applied = 0;
        var now = DateTimeOffset.UtcNow;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            foreach (var item in items)
            {
                if (item.Code is not { } code)
                {
                    continue; // UnknownRow：无法识别，忽略
                }

                decisions.TryGetValue(code, out var decision);
                var action = ParseAction(decision?.Action, item.Action);
                if (!item.AllowedActions.Contains(action))
                {
                    throw new InvalidOperationException($"基金 {code} 不支持的处理方式：{action}");
                }

                switch (action)
                {
                    case ReconcileAction.None:
                        changeKinds[code] = "Unchanged";
                        break;

                    case ReconcileAction.KeepSystem:
                        changeKinds[code] = "KeptSystem";
                        logs.Add($"保留系统值：{item.SystemName ?? item.FileName}（{code}）");
                        break;

                    case ReconcileAction.DividendReinvest:
                    {
                        var holding = await GetOrCreateHoldingAsync(code, proofDate, now, ct);
                        holding.Shares = TradeMath.AddShares(holding.Shares, item.SharesDelta);
                        holding.UpdatedAt = now;
                        changeKinds[code] = "DividendReinvest";
                        applied++;
                        logs.Add($"红利再投：{item.SystemName}（{code}）份额 +{item.SharesDelta:0.##}，本金不变");
                        break;
                    }

                    case ReconcileAction.ManualAdd:
                    {
                        var addedCost = decision?.AddedCost is { } c && c >= 0
                            ? c
                            : item.SuggestedAddedCost
                            ?? throw new InvalidOperationException(
                                $"基金 {code} 缺少最新净值，无法估算加仓本金，请在“增加本金”中填写实际金额");
                        var holding = await GetOrCreateHoldingAsync(code, proofDate, now, ct);
                        holding.Shares = TradeMath.AddShares(holding.Shares, item.SharesDelta);
                        holding.CostAmount = TradeMath.AddCost(holding.CostAmount ?? 0m, addedCost);
                        holding.UpdatedAt = now;
                        changeKinds[code] = "ManualAdd";
                        applied++;
                        logs.Add($"手动加仓：{item.SystemName}（{code}）份额 +{item.SharesDelta:0.##}，本金 +{addedCost:0.00}");
                        break;
                    }

                    case ReconcileAction.AcceptFileValue:
                    {
                        if (item.FileMarketValue is not { } mv)
                        {
                            throw new InvalidOperationException($"基金 {code} 的文件市值缺失，无法更新");
                        }
                        await UpsertManualValueAsync(code, mv, proofDate, now, ct);
                        changeKinds[code] = "ManualValue";
                        applied++;
                        logs.Add($"手工市值更新：{item.SystemName}（{code}）→ {mv:0.00}");
                        break;
                    }

                    case ReconcileAction.CreateFund:
                        await CreateFundFromImportAsync(item, decision, proofDate, now, logs, ct);
                        changeKinds[code] = "NewFund";
                        applied++;
                        break;
                }
            }

            // 先把应用变更落库（仍在同一事务内），随后生成的全量快照才能读到新基金/新持仓
            await db.SaveChangesAsync(ct);

            var version = await SaveVersionAsync(
                "import", statement.FileName, proofDate, changeKinds, now, ct);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            cache.Remove(TokenPrefix + req.Token);
            logger.LogInformation("导入确认完成 版本={Version} 应用 {Applied} 项", version.Version, applied);
            logs.Add($"持仓快照 {version.Version} 已落盘，看板与决策引擎已切换到新数据");

            return new CommitResultDto(
                version.Version, "import", proofDate.ToString("yyyy-MM-dd"),
                applied, logs, Summarize(items));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    // ---------- 版本列表 / 回滚 ----------

    public async Task<IReadOnlyList<VersionListItemDto>> ListVersionsAsync(CancellationToken ct)
    {
        var versions = await db.PositionVersions
            .AsNoTracking()
            .OrderByDescending(v => v.Id)
            .Select(v => new { v.Id, v.Version, v.Source, v.FileName, v.ProofDate, v.CreatedAt, v.IsCurrent, v.Summary, Count = v.Items.Count })
            .ToListAsync(ct);

        return versions
            .Select(v => new VersionListItemDto(
                v.Id, v.Version, v.Source, v.FileName,
                v.ProofDate.ToString("yyyy-MM-dd"),
                v.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                v.IsCurrent, v.Summary, v.Count))
            .ToList();
    }

    public async Task<CommitResultDto> RollbackAsync(long versionId, CancellationToken ct)
    {
        var target = await db.PositionVersions
            .Include(v => v.Items)
            .FirstOrDefaultAsync(v => v.Id == versionId, ct)
            ?? throw new InvalidOperationException("目标版本不存在");

        var now = DateTimeOffset.UtcNow;
        var logs = new List<string> { $"开始回滚到 {target.Version}（证明日期 {target.ProofDate:yyyy-MM-dd}）" };

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        try
        {
            var targetCodes = new HashSet<string>(target.Items.Select(i => i.FundCode), StringComparer.OrdinalIgnoreCase);

            foreach (var it in target.Items)
            {
                var fund = await db.Funds.FirstOrDefaultAsync(f => f.Code == it.FundCode, ct);
                if (fund is null)
                {
                    // 防御：版本中的基金档案已不存在则按快照冗余信息重建
                    fund = new Fund
                    {
                        Code = it.FundCode,
                        Name = it.FundName,
                        Type = it.FundType,
                        SectorId = it.SectorId,
                        UseManualValue = it.IsManual,
                        DividendMethod = "REINVEST",
                    };
                    db.Funds.Add(fund);
                }
                fund.IsActive = true;
                fund.UseManualValue = it.IsManual;

                if (it.IsManual)
                {
                    await UpsertManualValueAsync(it.FundCode, it.MarketValue, target.ProofDate, now, ct);
                    var oldHolding = await db.Holdings.FirstOrDefaultAsync(h => h.FundCode == it.FundCode, ct);
                    if (oldHolding is not null)
                    {
                        db.Holdings.Remove(oldHolding);
                    }
                }
                else
                {
                    var holding = await GetOrCreateHoldingAsync(it.FundCode, target.ProofDate, now, ct);
                    holding.Shares = it.Shares;
                    holding.CostAmount = it.CostAmount;
                    holding.UpdatedAt = now;
                    var oldManual = await db.ManualValues.FirstOrDefaultAsync(m => m.FundCode == it.FundCode, ct);
                    if (oldManual is not null)
                    {
                        db.ManualValues.Remove(oldManual);
                    }
                }
            }

            // 目标版本之后新增的基金：停用档案、清零持仓（不物理删除，保护历史流水/净值外键）
            var activeFunds = await db.Funds.Where(f => f.IsActive).ToListAsync(ct);
            foreach (var f in activeFunds.Where(f => !targetCodes.Contains(f.Code)))
            {
                f.IsActive = false;
                var h = await db.Holdings.FirstOrDefaultAsync(x => x.FundCode == f.Code, ct);
                if (h is not null)
                {
                    db.Holdings.Remove(h);
                }
                var m = await db.ManualValues.FirstOrDefaultAsync(x => x.FundCode == f.Code, ct);
                if (m is not null)
                {
                    db.ManualValues.Remove(m);
                }
                logs.Add($"已停用目标版本之后新增的标的：{f.Name}（{f.Code}）");
            }

            var rolledBack = new PositionVersion
            {
                Version = await NextVersionNoAsync(target.ProofDate, ct),
                Source = "rollback",
                FileName = null,
                ProofDate = target.ProofDate,
                IsCurrent = true,
                Summary = $"回滚到 {target.Version}（{target.Items.Count} 只标的）",
                Items = target.Items.Select(i => new PositionVersionItem
                {
                    FundCode = i.FundCode,
                    FundName = i.FundName,
                    FundType = i.FundType,
                    SectorId = i.SectorId,
                    IsManual = i.IsManual,
                    Shares = i.Shares,
                    CostAmount = i.CostAmount,
                    MarketValue = i.MarketValue,
                    ChangeKind = "RolledBack",
                }).ToList(),
            };
            await MarkOthersNotCurrentAsync(ct);
            db.PositionVersions.Add(rolledBack);

            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);

            logger.LogInformation("已回滚到版本 {Target}，新版本 {New}", target.Version, rolledBack.Version);
            logs.Add($"回滚完成，当前版本 {rolledBack.Version}");

            return new CommitResultDto(
                rolledBack.Version, "rollback", target.ProofDate.ToString("yyyy-MM-dd"),
                target.Items.Count, logs, new ImportSummaryDto(target.Items.Count, 0, 0, 0, 0));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// <summary>启动时若无任何版本，把当前持仓固化为 baseline（种子库即 v20260910-1）。</summary>
    public async Task EnsureBaselineAsync(CancellationToken ct)
    {
        if (await db.PositionVersions.AnyAsync(ct))
        {
            return;
        }

        var date = await db.FundNavs.AnyAsync(ct)
            ? (await db.FundNavs.MaxAsync(n => (DateOnly?)n.TradeDate, ct))!.Value
            : DateOnly.FromDateTime(DateTime.Now);

        var version = new PositionVersion
        {
            Version = await NextVersionNoAsync(date, ct),
            Source = "baseline",
            ProofDate = date,
            IsCurrent = true,
            Summary = "系统初始化基线（导入前持仓）",
        };
        var items = await SnapshotItemsAsync(new Dictionary<string, string>(), "Baseline", ct);
        version.Items = items;
        db.PositionVersions.Add(version);
        await db.SaveChangesAsync(ct);

        logger.LogInformation("已生成持仓基线版本 {Version}（{Count} 只标的）", version.Version, items.Count);
    }

    // ---------- 内部辅助 ----------

    private async Task<int?> FindStableSectorAsync(string namePart, CancellationToken ct) =>
        (await db.Sectors.AsNoTracking()
            .Where(s => !s.IsEquity && s.Name.Contains(namePart))
            .OrderBy(s => s.SortOrder)
            .FirstOrDefaultAsync(ct))?.Id;

    private async Task<(List<SystemPosition> System, int? BondSector, int? MoneySector, List<SectorOptionDto> Sectors)>
        LoadSystemAsync(CancellationToken ct)
    {
        var funds = await db.Funds.AsNoTracking().Include(f => f.Sector).ToListAsync(ct);
        var holdings = (await db.Holdings.AsNoTracking().ToListAsync(ct)).ToDictionary(h => h.FundCode);
        var manuals = (await db.ManualValues.AsNoTracking().ToListAsync(ct)).ToDictionary(m => m.FundCode);
        var latestNavs = (await db.FundNavs.AsNoTracking().ToListAsync(ct))
            .GroupBy(n => n.FundCode)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.TradeDate).First());

        int? bondSector = funds.Select(f => f.Sector).FirstOrDefault(s => s is { IsEquity: false } && s.Name.Contains("债"))?.Id;
        int? moneySector = funds.Select(f => f.Sector).FirstOrDefault(s => s is { IsEquity: false } && s.Name.Contains("货币"))?.Id;

        var system = new List<SystemPosition>();
        foreach (var f in funds.Where(f => f.IsActive))
        {
            holdings.TryGetValue(f.Code, out var h);
            manuals.TryGetValue(f.Code, out var mv);
            latestNavs.TryGetValue(f.Code, out var nav);
            var isManual = f.UseManualValue || f.Type == FundType.Money;
            var marketValue = isManual
                ? mv?.MarketValue ?? 0m
                : h is not null && nav is not null ? Math.Round(h.Shares * nav.UnitNav, 2) : 0m;

            system.Add(new SystemPosition(
                f.Code, f.Name, f.Type, f.SectorId, f.Sector?.Name, isManual,
                h?.Shares ?? 0m, h?.CostAmount, marketValue, nav?.UnitNav));
        }

        var sectors = (await db.Sectors.AsNoTracking().OrderBy(s => s.SortOrder).ToListAsync(ct))
            .Select(s => new SectorOptionDto(s.Id, s.Name, s.IsEquity))
            .ToList();

        return (system, bondSector, moneySector, sectors);
    }

    private async Task<Holding> GetOrCreateHoldingAsync(
        string code, DateOnly openedAt, DateTimeOffset now, CancellationToken ct)
    {
        var holding = await db.Holdings.FirstOrDefaultAsync(h => h.FundCode == code, ct);
        if (holding is null)
        {
            holding = new Holding
            {
                FundCode = code,
                Shares = 0m,
                CostAmount = 0m,
                OpenedAt = openedAt,
                UpdatedAt = now,
            };
            db.Holdings.Add(holding);
        }
        return holding;
    }

    private async Task UpsertManualValueAsync(
        string code, decimal marketValue, DateOnly valueDate, DateTimeOffset now, CancellationToken ct)
    {
        var manual = await db.ManualValues.FirstOrDefaultAsync(m => m.FundCode == code, ct);
        if (manual is null)
        {
            db.ManualValues.Add(new ManualValue
            {
                FundCode = code,
                MarketValue = marketValue,
                ValueDate = valueDate,
                UpdatedAt = now,
            });
        }
        else
        {
            manual.MarketValue = marketValue;
            manual.ValueDate = valueDate;
            manual.UpdatedAt = now;
        }
    }

    private async Task CreateFundFromImportAsync(
        ReconcileItem item, CommitDecisionDto? decision, DateOnly proofDate, DateTimeOffset now,
        List<string> logs, CancellationToken ct)
    {
        var code = item.Code!;
        var existing = await db.Funds.FirstOrDefaultAsync(f => f.Code == code, ct);
        if (existing is { IsActive: true })
        {
            throw new InvalidOperationException($"基金 {code} 已存在，请刷新对账结果");
        }
        var reactivated = existing is not null; // 回滚后被停用的标的随证明再次出现 → 复活档案

        var type = decision?.FundType is { } ft && Enum.TryParse<FundType>(ft, out var parsedType)
            ? parsedType
            : item.ProposedType ?? FundType.Stock;
        var isManual = type == FundType.Money;

        int? sectorId;
        if (isManual)
        {
            sectorId = item.SectorId ?? await FindStableSectorAsync("货币", ct);
        }
        else if (type == FundType.Bond)
        {
            sectorId = item.SectorId ?? await FindStableSectorAsync("债", ct);
        }
        else
        {
            sectorId = decision?.SectorId;
            var sector = sectorId is { } sid
                ? await db.Sectors.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct)
                : null;
            if (sector is null || !sector.IsEquity)
            {
                throw new InvalidOperationException($"新权益基金 {item.FileName}（{code}）必须选择一个权益赛道");
            }
        }

        var fund = existing ?? new Fund { Code = code, DividendMethod = "REINVEST" };
        fund.Name = string.IsNullOrWhiteSpace(item.FileName) ? code : item.FileName.Trim();
        fund.Type = type;
        fund.SectorId = sectorId;
        fund.UseManualValue = isManual;
        fund.IsActive = true;
        if (existing is null)
        {
            db.Funds.Add(fund);
        }

        var prefix = reactivated ? "重新纳入标的" : "新增基金建档";
        if (isManual)
        {
            var mv = item.FileMarketValue ?? 0m;
            await UpsertManualValueAsync(code, mv, proofDate, now, ct);
            logs.Add($"{prefix}：{item.FileName}（{code}），手工市值 {mv:0.00}，计入 D");
        }
        else
        {
            var shares = item.FileShares ?? 0m;
            var holding = await GetOrCreateHoldingAsync(code, proofDate, now, ct);
            holding.Shares = shares;
            holding.CostAmount = null; // 资产证明不含成本，收益指标后续由流水/手工补齐
            holding.UpdatedAt = now;
            var stable = type == FundType.Bond ? "稳健类（计入 D、不定投）" : "权益类（参与 B1-B4 判定）";
            logs.Add($"{prefix}：{item.FileName}（{code}）{shares:0.##} 份，归为{stable}");
        }
    }

    private async Task<PositionVersion> SaveVersionAsync(
        string source, string? fileName, DateOnly proofDate,
        Dictionary<string, string> changeKinds, DateTimeOffset now, CancellationToken ct)
    {
        await MarkOthersNotCurrentAsync(ct);
        var items = await SnapshotItemsAsync(changeKinds, "Unchanged", ct);
        var appliedCount = changeKinds.Count(kv =>
            kv.Value is "DividendReinvest" or "ManualAdd" or "NewFund" or "ManualValue");

        var version = new PositionVersion
        {
            Version = await NextVersionNoAsync(proofDate, ct),
            Source = source,
            FileName = fileName,
            ProofDate = proofDate,
            CreatedAt = now,
            IsCurrent = true,
            Summary = $"导入 {items.Count} 只标的，实际应用 {appliedCount} 项差异",
            Items = items,
        };
        db.PositionVersions.Add(version);
        return version;
    }

    /// <summary>从当前库状态生成全量版本镜像（应用变更后调用）。</summary>
    private async Task<List<PositionVersionItem>> SnapshotItemsAsync(
        Dictionary<string, string> changeKinds, string defaultKind, CancellationToken ct)
    {
        var funds = await db.Funds.Include(f => f.Sector).Where(f => f.IsActive).ToListAsync(ct);
        var holdings = (await db.Holdings.AsNoTracking().ToListAsync(ct)).ToDictionary(h => h.FundCode);
        var manuals = (await db.ManualValues.AsNoTracking().ToListAsync(ct)).ToDictionary(m => m.FundCode);
        var latestNavs = (await db.FundNavs.AsNoTracking().ToListAsync(ct))
            .GroupBy(n => n.FundCode)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.TradeDate).First());

        var items = new List<PositionVersionItem>();
        foreach (var f in funds)
        {
            holdings.TryGetValue(f.Code, out var h);
            manuals.TryGetValue(f.Code, out var mv);
            latestNavs.TryGetValue(f.Code, out var nav);
            var isManual = f.UseManualValue || f.Type == FundType.Money;
            var marketValue = isManual
                ? mv?.MarketValue ?? 0m
                : h is not null && nav is not null ? Math.Round(h.Shares * nav.UnitNav, 2) : 0m;

            items.Add(new PositionVersionItem
            {
                FundCode = f.Code,
                FundName = f.Name,
                FundType = f.Type,
                SectorId = f.SectorId,
                IsManual = isManual,
                Shares = h?.Shares ?? 0m,
                CostAmount = h?.CostAmount,
                MarketValue = marketValue,
                ChangeKind = changeKinds.TryGetValue(f.Code, out var k) ? k : defaultKind,
            });
        }
        return items;
    }

    private async Task MarkOthersNotCurrentAsync(CancellationToken ct)
    {
        await db.PositionVersions.Where(v => v.IsCurrent).ExecuteUpdateAsync(
            s => s.SetProperty(v => v.IsCurrent, false), ct);
    }

    private async Task<string> NextVersionNoAsync(DateOnly date, CancellationToken ct)
    {
        var prefix = "v" + date.ToString("yyyyMMdd") + "-";
        var sameDay = await db.PositionVersions.CountAsync(v => v.Version.StartsWith(prefix), ct);
        return prefix + (sameDay + 1);
    }

    private DateOnly ResolveProofDate(string? requestDate, DateOnly? statementDate)
    {
        if (requestDate is not null)
        {
            if (DateOnly.TryParse(requestDate, out var d))
            {
                return d;
            }
            throw new InvalidOperationException($"证明日期格式不正确：{requestDate}（应为 yyyy-MM-dd）");
        }
        if (statementDate is { } sd)
        {
            return sd;
        }
        return DateOnly.FromDateTime(DateTime.Now);
    }

    private static ReconcileAction ParseAction(string? raw, ReconcileAction fallback) =>
        raw is not null && Enum.TryParse<ReconcileAction>(raw, out var a) ? a : fallback;

    private static ImportSummaryDto Summarize(IReadOnlyList<ReconcileItem> items)
    {
        var matched = items.Count(i => i.Kind == ReconcileKind.Matched);
        return new ImportSummaryDto(
            items.Count,
            matched,
            items.Count - matched,
            items.Count(i => i.Kind == ReconcileKind.NewFund),
            items.Count(i => i.Kind == ReconcileKind.ManualValueChange));
    }

    private static ReconcileItemDto ToDto(ReconcileItem i) => new(
        i.Code,
        i.FileName,
        i.FileShares,
        i.FileMarketValue,
        i.ExistsInSystem,
        i.SystemName,
        i.SystemType,
        i.SectorId,
        i.SectorName,
        i.IsManual,
        i.SystemShares,
        i.SystemCostAmount,
        i.SystemMarketValue,
        i.UnitNav,
        i.Kind,
        i.Action,
        i.SharesDelta,
        i.SuggestedAddedCost,
        i.ProposedType,
        i.AllowedActions,
        i.Message);
}
