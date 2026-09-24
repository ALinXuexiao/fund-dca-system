using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M5_Proxy931069 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 数据修正（幂等）：931769 中证优选300 的代理指数
            // 由 000991（东财实际为「全指医药」，错配）改为 931069 中金300（东财有实时行情）。
            migrationBuilder.Sql("""
                INSERT INTO indexes (code, name, metric, low_threshold_percent, high_threshold_percent, window_years, proxy_code)
                VALUES ('931069', '中金300', 'PeTtm', 30, 70, 10, NULL)
                ON CONFLICT (code) DO NOTHING;
                """);
            migrationBuilder.Sql("UPDATE indexes SET proxy_code = '931069' WHERE code = '931769';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE indexes SET proxy_code = '000991' WHERE code = '931769';");
        }
    }
}
