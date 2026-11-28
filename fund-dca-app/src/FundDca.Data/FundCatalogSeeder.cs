using System.Text.Json;
using FundDca.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace FundDca.Data;

/// <summary>
/// 基金目录（参考数据）播种：赛道、指数、基金档案。
/// 与 <see cref="DbSeeder"/> 的分工：
/// <list type="bullet">
/// <item>目录每次启动都执行，逐条幂等——缺的补上，已有的不覆盖；</item>
/// <item>已有档案只回填为空的跟踪指数/申购费率（导入自动建档时最容易缺这两项）；</item>
/// <item>已有档案的赛道以目录为准（系统没有改赛道的接口，赛道调整只能改目录 JSON）。</item>
/// </list>
/// 数据源是内嵌的 Catalog/fund-catalog.json：新增基金只改 JSON，不再产生数据库迁移记录。
/// </summary>
public static class FundCatalogSeeder
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static async Task SeedAsync(FundDcaDbContext db, CancellationToken ct = default)
    {
        var catalog = await LoadAsync(ct);

        // ---------- 赛道：按名称判重；显式 Id 仅在未被占用时使用 ----------
        var sectors = await db.Sectors.ToListAsync(ct);
        var usedIds = sectors.Select(s => s.Id).ToHashSet();
        foreach (var s in catalog.Sectors)
        {
            if (sectors.Any(x => x.Name == s.Name))
            {
                continue;
            }
            var useExplicitId = s.Id != 0 && !usedIds.Contains(s.Id);
            db.Sectors.Add(new Sector
            {
                Id = useExplicitId ? s.Id : 0,
                Name = s.Name,
                LimitPercent = s.LimitPercent,
                IsEquity = s.IsEquity,
                SortOrder = s.SortOrder,
            });
            if (useExplicitId)
            {
                usedIds.Add(s.Id);
            }
        }
        await db.SaveChangesAsync(ct);

        // ---------- 指数：按代码判重，不覆盖用户已调过的阈值/代理 ----------
        var indexCodes = (await db.Indexes.Select(i => i.Code).ToListAsync(ct))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var i in catalog.Indexes)
        {
            if (indexCodes.Contains(i.Code))
            {
                continue;
            }
            db.Indexes.Add(new IndexInfo
            {
                Code = i.Code,
                Name = i.Name,
                Metric = ParseEnum<ValuationMetric>(i.Metric, $"指数 {i.Code}"),
                LowThresholdPercent = i.LowThresholdPercent,
                HighThresholdPercent = i.HighThresholdPercent,
                WindowYears = i.WindowYears,
                ProxyCode = string.IsNullOrWhiteSpace(i.ProxyCode) ? null : i.ProxyCode,
            });
            indexCodes.Add(i.Code);
        }
        await db.SaveChangesAsync(ct);

        // ---------- 基金：缺则插；已有只回填空字段 + 对齐赛道 ----------
        var sectorIdByName = await db.Sectors.ToDictionaryAsync(s => s.Name, s => s.Id, ct);
        var funds = await db.Funds.ToListAsync(ct);
        foreach (var f in catalog.Funds)
        {
            int? sectorId = null;
            if (!string.IsNullOrWhiteSpace(f.Sector))
            {
                if (!sectorIdByName.TryGetValue(f.Sector, out var sid))
                {
                    throw new InvalidOperationException(
                        $"基金目录 {f.Code} 引用了不存在的赛道「{f.Sector}」，请检查 fund-catalog.json");
                }
                sectorId = sid;
            }

            var existing = funds.FirstOrDefault(x => x.Code == f.Code);
            if (existing is not null)
            {
                if (existing.TrackedIndexCode is null && !string.IsNullOrWhiteSpace(f.TrackedIndexCode))
                {
                    existing.TrackedIndexCode = f.TrackedIndexCode;
                }
                if (existing.BuyFeeRate is null && f.BuyFeeRate is not null)
                {
                    existing.BuyFeeRate = f.BuyFeeRate;
                }
                if (sectorId is { } targetSectorId && existing.SectorId != targetSectorId)
                {
                    existing.SectorId = targetSectorId;
                }
                continue;
            }

            db.Funds.Add(new Fund
            {
                Code = f.Code,
                Name = f.Name,
                Type = ParseEnum<FundType>(f.Type, $"基金 {f.Code}"),
                SectorId = sectorId,
                TrackedIndexCode = string.IsNullOrWhiteSpace(f.TrackedIndexCode) ? null : f.TrackedIndexCode,
                BuyFeeRate = f.BuyFeeRate,
                DividendMethod = string.IsNullOrWhiteSpace(f.DividendMethod) ? "REINVEST" : f.DividendMethod,
                UseManualValue = f.UseManualValue,
                IsActive = true,
            });
        }
        await db.SaveChangesAsync(ct);
    }

    private static async Task<CatalogDocument> LoadAsync(CancellationToken ct)
    {
        var assembly = typeof(FundCatalogSeeder).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("fund-catalog.json", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("未找到内嵌资源 fund-catalog.json");

        await using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"无法读取内嵌资源 {resourceName}");
        var document = await JsonSerializer.DeserializeAsync<CatalogDocument>(stream, JsonOptions, ct);
        return document ?? throw new InvalidOperationException("基金目录 fund-catalog.json 解析为空");
    }

    private static TEnum ParseEnum<TEnum>(string value, string owner) where TEnum : struct, Enum =>
        Enum.TryParse<TEnum>(value, ignoreCase: true, out var parsed)
            ? parsed
            : throw new InvalidOperationException($"{owner} 的类型/口径「{value}」无法识别");

    private sealed record CatalogDocument(
        List<CatalogSector> Sectors,
        List<CatalogIndex> Indexes,
        List<CatalogFund> Funds);

    private sealed record CatalogSector(
        int Id, string Name, decimal LimitPercent, bool IsEquity, int SortOrder);

    private sealed record CatalogIndex(
        string Code, string Name, string Metric, decimal LowThresholdPercent,
        decimal HighThresholdPercent, int WindowYears, string? ProxyCode);

    private sealed record CatalogFund(
        string Code, string Name, string Type, string? Sector, string? TrackedIndexCode,
        decimal? BuyFeeRate, string? DividendMethod, bool UseManualValue);
}
