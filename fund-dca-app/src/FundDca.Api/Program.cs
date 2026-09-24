using System.Text.Json.Serialization;
using FundDca.Api.Services;
using FundDca.Collect;
using FundDca.Data;
using FundDca.Import.Parsing;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// 控制器；枚举以字符串输出，前端无需维护魔法数字
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

builder.Services.AddOpenApi();

// PostgreSQL 主库：连接串只存在于后端配置，前端永不直连数据库。
// 上云后把 ConnectionStrings:FundDca 指向 Neon（含 SSL Mode=Require），
// 或在各机器用环境变量 ConnectionStrings__FundDca 覆盖，无需改代码。
var primaryConnString = builder.Configuration.GetConnectionString("FundDca")
    ?? throw new InvalidOperationException("缺少连接串 ConnectionStrings:FundDca");

// 本地镜像（可选）：主库写入成功后把数据整表同步到本机 fund_dca 库；
// 本机没有本地库（未装 PostgreSQL/库不存在/连不上）时自动跳过；
// 镜像与主库指向同一数据库时（开发机尚未上云）自动禁用。
var mirrorEnabled = builder.Configuration.GetValue("Mirror:Enabled", true);
var mirrorConnString = mirrorEnabled
    ? builder.Configuration.GetConnectionString("FundDcaMirror")
    : null;

builder.Services.AddSingleton(sp => new LocalMirrorSynchronizer(
    primaryConnString, mirrorConnString, sp.GetRequiredService<ILogger<LocalMirrorSynchronizer>>()));
builder.Services.AddSingleton<MirrorSyncSaveChangesInterceptor>();

builder.Services.AddDbContext<FundDcaDbContext>((sp, o) => o
    .UseNpgsql(primaryConnString)
    .AddInterceptors(sp.GetRequiredService<MirrorSyncSaveChangesInterceptor>()));

// 天天基金净值采集（5 次静默重试，间隔由 appsettings 配置）与看板聚合
builder.Services.AddFundDcaCollector(builder.Configuration);
builder.Services.AddMemoryCache();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<DecisionService>();
builder.Services.AddScoped<TradeService>();
builder.Services.AddScoped<ImportService>();

// M3 资产证明解析器（无状态，单例）：按扩展名路由 CSV → Excel → PdfPig
builder.Services.AddSingleton<CsvStatementParser>();
builder.Services.AddSingleton<ExcelStatementParser>();
builder.Services.AddSingleton<PdfStatementParser>();
builder.Services.AddSingleton<IStatementParser>(sp => sp.GetRequiredService<CsvStatementParser>());
builder.Services.AddSingleton<IStatementParser>(sp => sp.GetRequiredService<ExcelStatementParser>());
builder.Services.AddSingleton<IStatementParser>(sp => sp.GetRequiredService<PdfStatementParser>());
builder.Services.AddSingleton<StatementParserRegistry>();

// 本机开发跨域（Vite 默认 5173）；上云后在此收紧为正式域名
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins("http://localhost:5173", "http://127.0.0.1:5173")
        .AllowAnyHeader()
        .AllowAnyMethod()));

var app = builder.Build();

// 启动即迁移并写入种子数据（M0 便利做法；M5 改为发布流程执行）
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FundDcaDbContext>();
    await DbSeeder.SeedAsync(db);
    app.Logger.LogInformation("数据库就绪：基金 {Funds} 只，指数 {Indexes} 个",
        await db.Funds.CountAsync(), await db.Indexes.CountAsync());

    // M3：首次启动把当前持仓固化为基线版本，供导入对账回滚
    var importService = scope.ServiceProvider.GetRequiredService<ImportService>();
    await importService.EnsureBaselineAsync(default);

    // 启动即同步一次本地镜像（本机无本地库时自动跳过并告警，不影响启动）
    var synchronizer = scope.ServiceProvider.GetRequiredService<LocalMirrorSynchronizer>();
    await synchronizer.SyncNowAsync();
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseCors();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.Now }));

app.Run();
