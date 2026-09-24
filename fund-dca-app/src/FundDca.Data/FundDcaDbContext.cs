using FundDca.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace FundDca.Data;

/// <summary>
/// EF Core 数据上下文（PostgreSQL / Npgsql）。
/// 表名、列名统一 snake_case，便于将来在 psql / 云数据库中直接查看。
/// </summary>
public class FundDcaDbContext : DbContext
{
    public FundDcaDbContext(DbContextOptions<FundDcaDbContext> options) : base(options)
    {
    }

    public DbSet<Fund> Funds => Set<Fund>();
    public DbSet<IndexInfo> Indexes => Set<IndexInfo>();
    public DbSet<Sector> Sectors => Set<Sector>();
    public DbSet<OverlapGroup> OverlapGroups => Set<OverlapGroup>();
    public DbSet<FundOverlap> FundOverlaps => Set<FundOverlap>();
    public DbSet<Holding> Holdings => Set<Holding>();
    public DbSet<FundNav> FundNavs => Set<FundNav>();
    public DbSet<ManualValue> ManualValues => Set<ManualValue>();
    public DbSet<DailySnapshot> DailySnapshots => Set<DailySnapshot>();
    public DbSet<BudgetMonth> BudgetMonths => Set<BudgetMonth>();
    public DbSet<IndexValuation> IndexValuations => Set<IndexValuation>();
    public DbSet<DcaSetting> DcaSettings => Set<DcaSetting>();
    public DbSet<Trade> Trades => Set<Trade>();
    public DbSet<PositionVersion> PositionVersions => Set<PositionVersion>();
    public DbSet<PositionVersionItem> PositionVersionItems => Set<PositionVersionItem>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ---------- 指数 ----------
        b.Entity<IndexInfo>(e =>
        {
            e.ToTable("indexes");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(20);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Metric).HasConversion<string>().HasMaxLength(16);
            e.Property(x => x.LowThresholdPercent).HasPrecision(5, 2);
            e.Property(x => x.HighThresholdPercent).HasPrecision(5, 2);
            e.Property(x => x.ProxyCode).HasMaxLength(20);
        });

        // ---------- 赛道 ----------
        b.Entity<Sector>(e =>
        {
            e.ToTable("sectors");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(32).IsRequired();
            e.Property(x => x.LimitPercent).HasPrecision(5, 2);
            e.HasIndex(x => x.Name).IsUnique();
        });

        // ---------- 基金 ----------
        var tiersJsonOptions = new System.Text.Json.JsonSerializerOptions();
        var tiersConverter = new ValueConverter<List<RedeemFeeTier>, string>(
            v => System.Text.Json.JsonSerializer.Serialize(v, tiersJsonOptions),
            v => System.Text.Json.JsonSerializer.Deserialize<List<RedeemFeeTier>>(v, tiersJsonOptions) ?? new List<RedeemFeeTier>());
        var tiersComparer = new ValueComparer<List<RedeemFeeTier>>(
            (a, c) => System.Text.Json.JsonSerializer.Serialize(a, tiersJsonOptions)
                     == System.Text.Json.JsonSerializer.Serialize(c, tiersJsonOptions),
            v => v == null ? 0 : System.Text.Json.JsonSerializer.Serialize(v, tiersJsonOptions).GetHashCode(),
            v => System.Text.Json.JsonSerializer.Deserialize<List<RedeemFeeTier>>(
                System.Text.Json.JsonSerializer.Serialize(v, tiersJsonOptions), tiersJsonOptions)!);

        b.Entity<Fund>(e =>
        {
            e.ToTable("funds");
            e.HasKey(x => x.Code);
            e.Property(x => x.Code).HasMaxLength(12);
            e.Property(x => x.Name).HasMaxLength(64).IsRequired();
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.TrackedIndexCode).HasMaxLength(20);
            e.Property(x => x.BuyFeeRate).HasPrecision(6, 4);
            e.Property(x => x.DividendMethod).HasMaxLength(8).HasDefaultValue("REINVEST");
            e.Property(x => x.RedeemFeeTiers)
                .HasConversion(tiersConverter, tiersComparer)
                .HasColumnType("jsonb")
                .HasColumnName("redeem_fee_tiers");

            e.HasOne(x => x.TrackedIndex)
                .WithMany()
                .HasForeignKey(x => x.TrackedIndexCode)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasOne(x => x.Sector)
                .WithMany()
                .HasForeignKey(x => x.SectorId)
                .OnDelete(DeleteBehavior.SetNull);

            e.HasIndex(x => x.TrackedIndexCode);
            e.HasIndex(x => x.SectorId);
        });

        // ---------- 去重组 ----------
        b.Entity<OverlapGroup>(e =>
        {
            e.ToTable("overlap_groups");
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(32).IsRequired();
            e.HasIndex(x => x.Name).IsUnique();
        });

        b.Entity<FundOverlap>(e =>
        {
            e.ToTable("fund_overlaps");
            e.HasKey(x => new { x.OverlapGroupId, x.FundCode });
            e.Property(x => x.FundCode).HasMaxLength(12);

            e.HasOne(x => x.Group)
                .WithMany(g => g.Members)
                .HasForeignKey(x => x.OverlapGroupId)
                .OnDelete(DeleteBehavior.Cascade);

            e.HasOne(x => x.Fund)
                .WithMany()
                .HasForeignKey(x => x.FundCode)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- 当前持仓 ----------
        b.Entity<Holding>(e =>
        {
            e.ToTable("holdings");
            e.HasKey(x => x.FundCode);
            e.Property(x => x.FundCode).HasMaxLength(12);
            e.Property(x => x.Shares).HasPrecision(18, 4);
            e.Property(x => x.CostAmount).HasPrecision(18, 4);
            e.HasOne(x => x.Fund)
                .WithOne()
                .HasForeignKey<Holding>(x => x.FundCode)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- 净值历史 ----------
        b.Entity<FundNav>(e =>
        {
            e.ToTable("fund_navs");
            e.HasKey(x => new { x.FundCode, x.TradeDate });
            e.Property(x => x.FundCode).HasMaxLength(12);
            e.Property(x => x.UnitNav).HasPrecision(12, 5);
            e.Property(x => x.AccNav).HasPrecision(12, 5);
            e.Property(x => x.DayChangePercent).HasPrecision(8, 4);
            e.Property(x => x.Source).HasMaxLength(16);
            e.HasOne(x => x.Fund)
                .WithMany()
                .HasForeignKey(x => x.FundCode)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.FundCode);
        });

        // ---------- 手工市值（货币基金） ----------
        b.Entity<ManualValue>(e =>
        {
            e.ToTable("manual_values");
            e.HasKey(x => x.FundCode);
            e.Property(x => x.FundCode).HasMaxLength(12);
            e.Property(x => x.MarketValue).HasPrecision(18, 4);
            e.HasOne(x => x.Fund)
                .WithOne()
                .HasForeignKey<ManualValue>(x => x.FundCode)
                .OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- 每日快照 ----------
        b.Entity<DailySnapshot>(e =>
        {
            e.ToTable("daily_snapshots");
            e.HasKey(x => new { x.FundCode, x.TradeDate });
            e.Property(x => x.FundCode).HasMaxLength(12);
            e.Property(x => x.Shares).HasPrecision(18, 4);
            e.Property(x => x.UnitNav).HasPrecision(12, 5);
            e.Property(x => x.MarketValue).HasPrecision(18, 4);
            e.Property(x => x.DayChangePercent).HasPrecision(8, 4);
            e.HasOne(x => x.Fund)
                .WithMany()
                .HasForeignKey(x => x.FundCode)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TradeDate);
        });

        // ---------- 月度预算 ----------
        b.Entity<BudgetMonth>(e =>
        {
            e.ToTable("budget_months");
            e.HasKey(x => x.YearMonth);
            e.Property(x => x.YearMonth).HasMaxLength(7);
            e.Property(x => x.BudgetAmount).HasPrecision(18, 4);
            e.Property(x => x.InvestedAmount).HasPrecision(18, 4);
        });

        // ---------- 指数估值日快照（B1） ----------
        b.Entity<IndexValuation>(e =>
        {
            e.ToTable("index_valuations");
            e.HasKey(x => new { x.IndexCode, x.TradeDate });
            e.Property(x => x.IndexCode).HasMaxLength(20);
            e.Property(x => x.PeTtm).HasPrecision(10, 4);
            e.Property(x => x.PePercentile).HasPrecision(6, 2);
            e.Property(x => x.Pb).HasPrecision(10, 4);
            e.Property(x => x.PbPercentile).HasPrecision(6, 2);
            e.Property(x => x.ResolvedCode).HasMaxLength(20);
            e.Property(x => x.Source).HasMaxLength(16);
            e.HasOne(x => x.Index)
                .WithMany()
                .HasForeignKey(x => x.IndexCode)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.TradeDate);
        });

        // ---------- 定投全局设置（单行） ----------
        b.Entity<DcaSetting>(e =>
        {
            e.ToTable("dca_settings");
            e.HasKey(x => x.Id);
            e.Property(x => x.FixedInvestAmount).HasPrecision(10, 2);
        });

        // ---------- 交易流水（买入） ----------
        b.Entity<Trade>(e =>
        {
            e.ToTable("trades");
            e.HasKey(x => x.Id);
            e.Property(x => x.FundCode).HasMaxLength(12);
            e.Property(x => x.Side).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.Amount).HasPrecision(18, 4);
            e.Property(x => x.EstimatedShares).HasPrecision(18, 4);
            e.Property(x => x.ConfirmedShares).HasPrecision(18, 4);
            e.Property(x => x.ConfirmedNav).HasPrecision(12, 5);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            e.HasOne(x => x.Fund)
                .WithMany()
                .HasForeignKey(x => x.FundCode)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => new { x.Status, x.TradeDate });
            e.HasIndex(x => x.FundCode);
        });

        // ---------- 持仓版本快照（M3 导入对账：原子切换 + 一键回滚） ----------
        b.Entity<PositionVersion>(e =>
        {
            e.ToTable("position_versions");
            e.HasKey(x => x.Id);
            e.Property(x => x.Version).HasMaxLength(20).IsRequired();
            e.HasIndex(x => x.Version).IsUnique();
            e.Property(x => x.Source).HasMaxLength(16);
            e.Property(x => x.FileName).HasMaxLength(256);
            e.Property(x => x.Summary).HasMaxLength(1024);
            e.HasMany(x => x.Items)
                .WithOne(x => x.Version!)
                .HasForeignKey(x => x.VersionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.HasIndex(x => x.IsCurrent);
        });

        b.Entity<PositionVersionItem>(e =>
        {
            e.ToTable("position_version_items");
            e.HasKey(x => x.Id);
            e.Property(x => x.FundCode).HasMaxLength(12);
            e.Property(x => x.FundName).HasMaxLength(64);
            e.Property(x => x.FundType).HasConversion<string>().HasMaxLength(8);
            e.Property(x => x.Shares).HasPrecision(18, 4);
            e.Property(x => x.CostAmount).HasPrecision(18, 4);
            e.Property(x => x.MarketValue).HasPrecision(18, 4);
            e.Property(x => x.ChangeKind).HasMaxLength(20);
            e.HasIndex(x => x.VersionId);
        });

        // 列名统一 snake_case（零三方依赖；jsonb 等显式 HasColumnName 优先保留）
        foreach (var entity in b.Model.GetEntityTypes())
        {
            foreach (var property in entity.GetProperties())
            {
                property.SetColumnName(ToSnakeCase(property.Name));
            }
        }
    }

    /// <summary>PascalCase/camelCase → snake_case，如 TrackedIndexCode → tracked_index_code。</summary>
    private static string ToSnakeCase(string name) =>
        System.Text.RegularExpressions.Regex.Replace(name, "([a-z0-9])([A-Z])", "$1_$2").ToLowerInvariant();
}
