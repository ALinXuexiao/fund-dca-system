using System.Text.Json.Serialization;
using FundDca.Api.Services;
using FundDca.Collect;
using FundDca.Data;
using FundDca.Import.Parsing;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);

// JSON 响应压缩（看板/决策/盘中估算接口每次加载与 60 秒轮询都返回全量 DTO；
// 主库在云端、将来前端静态托管后浏览器到后端也是公网链路，gzip/br 可把 KB 级 JSON 压到 1/5 以下）
builder.Services.AddResponseCompression(o =>
{
    o.EnableForHttps = true;
    o.MimeTypes = ["application/json", "text/plain", "text/css", "application/javascript"];
});

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
builder.Services.AddSingleton<IndexQuoteFetchState>();

// 无人值守自退出：心跳接口只记时间戳，看门狗在长时间无心跳时优雅停止主机。
// 默认关闭（appsettings），启动入口用环境变量 HostLifetime__ExitWithoutHeartbeat=true 开启。
builder.Services.Configure<HostLifetimeOptions>(builder.Configuration.GetSection(HostLifetimeOptions.SectionName));
builder.Services.AddSingleton<HostHeartbeatMonitor>();
builder.Services.AddHostedService<HostExitWatchdog>();
builder.Services.AddScoped<DashboardService>();
builder.Services.AddScoped<IntradayService>();
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

// 启动即迁移并写入种子数据（M0 便利做法；M5 改为发布流程执行）。
// 云端库在跨境链路上会出现握手丢包、或解析到不可达边缘后整段读超时：
// 这类失败重试即可跨过，不能让一次握手失败直接结束进程——否则前端永远停在"后端未就绪"。
// 单次尝试必须有硬超时：连接半开时 Npgsql 的读会一直挂着而不抛异常，没有超时就等不到重试。
const int startupAttempts = 5;
var attemptTimeout = TimeSpan.FromSeconds(45);
for (var attempt = 1; ; attempt++)
{
    try
    {
        using var attemptCts = new CancellationTokenSource(attemptTimeout);
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FundDcaDbContext>();
        await DbSeeder.SeedAsync(db, attemptCts.Token);
        app.Logger.LogInformation("数据库就绪：基金 {Funds} 只，指数 {Indexes} 个",
            await db.Funds.CountAsync(attemptCts.Token), await db.Indexes.CountAsync(attemptCts.Token));

        // M3：首次启动把当前持仓固化为基线版本，供导入对账回滚
        var importService = scope.ServiceProvider.GetRequiredService<ImportService>();
        await importService.EnsureBaselineAsync(attemptCts.Token);

        // 启动即同步一次本地镜像（本机无本地库时自动跳过并告警，不影响启动）
        var synchronizer = scope.ServiceProvider.GetRequiredService<LocalMirrorSynchronizer>();
        await synchronizer.SyncNowAsync();
        break;
    }
    catch (Exception ex) when (attempt < startupAttempts && IsTransientDbFailure(ex))
    {
        app.Logger.LogWarning("数据库连接失败（第 {Attempt}/{Total} 次尝试，单次上限 {Timeout} 秒）：{Reason}；3 秒后重试",
            attempt, startupAttempts, attemptTimeout.TotalSeconds, ex.GetBaseException().Message);
        await Task.Delay(TimeSpan.FromSeconds(3));
    }
}

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseResponseCompression();
app.UseCors();
app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "ok", time = DateTimeOffset.Now }));

app.Run();

/// <summary>
/// 判断启动期的数据库失败是否属于可重试的链路问题。
/// 服务端明确返回的 SQL 错误（如迁移脚本本身有问题）重试无意义，直接抛出。
/// </summary>
static bool IsTransientDbFailure(Exception ex) => ex switch
{
    PostgresException => false,
    NpgsqlException { InnerException: PostgresException } => false,
    // 断流、读超时、SSL 握手失败、主机不可达
    NpgsqlException => true,
    TimeoutException => true,
    // 单次尝试的硬超时（连接半开时只能靠它打断）
    OperationCanceledException => true,
    System.Net.Sockets.SocketException => true,
    _ => ex.InnerException is not null && IsTransientDbFailure(ex.InnerException),
};
