using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FundDca.Collect;

public static class CollectorServiceExtensions
{
    public static IServiceCollection AddFundDcaCollector(
        this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<CollectorOptions>(
            configuration.GetSection(CollectorOptions.SectionName));

        services.AddHttpClient<INavQuoteSource, EastMoneyNavSource>((sp, client) =>
        {
            var timeout = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value.TimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(timeout);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        services.AddHttpClient<DanJuanValuationSource>((sp, client) =>
        {
            var timeout = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value.TimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(timeout);
            client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        });

        // 指数实时行情（盘中估算用）：超时更短，盘中每 60 秒会刷新一次。
        // 连接层强制 IPv4 优先：push2.eastmoney.com 的 IPv6 边缘对 api/qt/ulist.np/get
        // 会直接断连（.NET 收到空响应 ResponseEnded 且不自动回退 IPv4），
        // 故在 ConnectCallback 里自己排序 DNS 结果，IPv4 不可用时仍保留 IPv6 兜底。
        services.AddHttpClient<EastMoneyIndexQuoteSource>((sp, client) =>
        {
            var timeout = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value.TimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(timeout);
        }).ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
        {
            ConnectCallback = ConnectQuoteAsync,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        });

        // 中证官网指数自有 PE 历史（蛋卷未覆盖指数的估值兜底）
        services.AddHttpClient<CsiIndexPeSource>((sp, client) =>
        {
            var timeout = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value.TimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(timeout);
        });

        services.AddSingleton<NavCollector>();
        services.AddScoped<RefreshService>();
        services.AddScoped<ValuationRefreshService>();
        return services;
    }

    /// <summary>
    /// 连接层地址选择：
    /// 1) 请求层若通过 <see cref="EastMoneyIndexQuoteSource.PinnedAddress"/> 指定了地址，就只连它
    ///    （跨地址轮换重试由 EastMoneyIndexQuoteSource 负责，这里不自行换地址）；
    /// 2) 未指定时退回 IPv4 优先、IPv6 兜底。
    /// 之所以不在这里做“响应被掐断后再换地址”，是因为连接层拿不到“响应中途失败”的信号，
    /// 只有请求层能拿到 HttpIOException(ResponseEnded) 并重试。
    /// </summary>
    private static async ValueTask<Stream> ConnectQuoteAsync(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        if (context.InitialRequestMessage.Options.TryGetValue(
                EastMoneyIndexQuoteSource.PinnedAddress, out var pinned) && pinned is not null)
        {
            addresses = [pinned];
        }
        else if (IPAddress.TryParse(host, out var literal))
        {
            addresses = [literal];
        }
        else
        {
            addresses = await Dns.GetHostAddressesAsync(host, ct);
        }

        var ordered = addresses
            .OrderBy(a => a.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        Exception? lastError = null;
        foreach (var address in ordered)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            {
                NoDelay = true,
            };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
                socket.Dispose();
            }
        }

        throw lastError ?? new SocketException((int)SocketError.HostUnreachable);
    }
}
