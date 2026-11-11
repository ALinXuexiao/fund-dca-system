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
            ConnectCallback = ConnectPreferIpv4Async,
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
    /// 连接层地址排序：IPv4 优先、IPv6 兜底。Windows/.NET 默认按 DNS 顺序优先连 IPv6，
    /// 而东财 push2 的 IPv6 边缘会在 HTTP 层直接断连（TCP/TLS 成功、无响应），
    /// 此时 HttpClient 不会换地址重试，因此在这里把 IPv4 提到前面。
    /// </summary>
    private static async ValueTask<Stream> ConnectPreferIpv4Async(
        SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var host = context.DnsEndPoint.Host;
        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out var literal))
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
