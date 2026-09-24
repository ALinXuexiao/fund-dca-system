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

        services.AddSingleton<NavCollector>();
        services.AddScoped<RefreshService>();
        services.AddScoped<ValuationRefreshService>();
        return services;
    }
}
