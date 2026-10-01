using FundDca.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace FundDca.Data;

/// <summary>
/// 行情类"每组最新一行"查询。
/// 历史表（fund_navs / index_valuations / daily_snapshots）会无限增长，
/// 看板/决策/盘中估算只需要每个标的最新一条；用 PostgreSQL 的 DISTINCT ON 一次取回，
/// 替代"整表拉到应用层再 GroupBy"——主库在 Neon 云端时，可把每请求的跨网传输量从
/// 全量历史（随交易日线性增长）降为标的数量级。
/// </summary>
public static class MarketDataQueries
{
    /// <summary>每只基金最新一条净值（按交易日倒序取首行），无跟踪。</summary>
    public static Task<List<FundNav>> LatestNavsAsync(
        this FundDcaDbContext db, CancellationToken ct = default) =>
        db.FundNavs
            .FromSqlRaw(
                "SELECT DISTINCT ON (fund_code) * FROM fund_navs " +
                "ORDER BY fund_code, trade_date DESC")
            .AsNoTracking()
            .ToListAsync(ct);

    /// <summary>每个指数最新一条估值快照，无跟踪。</summary>
    public static Task<List<IndexValuation>> LatestValuationsAsync(
        this FundDcaDbContext db, CancellationToken ct = default) =>
        db.IndexValuations
            .FromSqlRaw(
                "SELECT DISTINCT ON (index_code) * FROM index_valuations " +
                "ORDER BY index_code, trade_date DESC")
            .AsNoTracking()
            .ToListAsync(ct);

    /// <summary>
    /// 每只基金最新一条每日快照（跟踪实体，供写入链路按最新日期判断 update/insert）。
    /// 采集到的行情日期只会等于或晚于库内最新日期：相等则更新该行，新日期则由调用方插入。
    /// </summary>
    public static Task<List<DailySnapshot>> LatestSnapshotsTrackedAsync(
        this FundDcaDbContext db, CancellationToken ct = default) =>
        db.DailySnapshots
            .FromSqlRaw(
                "SELECT DISTINCT ON (fund_code) * FROM daily_snapshots " +
                "ORDER BY fund_code, trade_date DESC")
            .ToListAsync(ct);

    /// <summary>每只基金最新一条净值（跟踪实体，供采集 upsert 复用）。</summary>
    public static Task<List<FundNav>> LatestNavsTrackedAsync(
        this FundDcaDbContext db, CancellationToken ct = default) =>
        db.FundNavs
            .FromSqlRaw(
                "SELECT DISTINCT ON (fund_code) * FROM fund_navs " +
                "ORDER BY fund_code, trade_date DESC")
            .ToListAsync(ct);

    /// <summary>每个指数最新一条估值快照（跟踪实体，供采集 upsert 复用）。</summary>
    public static Task<List<IndexValuation>> LatestValuationsTrackedAsync(
        this FundDcaDbContext db, CancellationToken ct = default) =>
        db.IndexValuations
            .FromSqlRaw(
                "SELECT DISTINCT ON (index_code) * FROM index_valuations " +
                "ORDER BY index_code, trade_date DESC")
            .ToListAsync(ct);
}
