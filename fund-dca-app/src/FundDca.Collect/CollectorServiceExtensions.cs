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

        // 指数实时行情（盘中估算用）：超时更短，盘中每 60 秒会刷新一次
        services.AddHttpClient<EastMoneyIndexQuoteSource>((sp, client) =>
        {
            var timeout = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<CollectorOptions>>().Value.TimeoutSeconds;
            client.Timeout = TimeSpan.FromSeconds(timeout);
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
}
