using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M6_FixTrackedIndex931069 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 501060 跟踪指数代码修正：原 931769 经中证官网核实为「中证新经济债券指数」（档案错记），
            // 基金产品资料概要载明的标的指数「中证中金优选300」真实代码为 931069（东财有实时行情）。
            migrationBuilder.Sql("UPDATE funds SET tracked_index_code = '931069' WHERE code = '501060';");

            // 历史 PE 估值迁到正确指数代码；只迁带 PE 的行，避免把债券指数的异常数据带过来
            migrationBuilder.Sql("""
                UPDATE index_valuations SET index_code = '931069'
                WHERE index_code = '931769' AND pe_ttm IS NOT NULL;
                """);

            // 删除错误口径的指数档案（931069 已由 M5 幂等补入）
            migrationBuilder.Sql("DELETE FROM indexes WHERE code = '931769';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO indexes (code, name, metric, low_threshold_percent, high_threshold_percent, window_years, proxy_code)
                VALUES ('931769', '中证优选300', 'PeTtm', 30, 70, 10, NULL)
                ON CONFLICT (code) DO NOTHING;
                """);
            migrationBuilder.Sql("UPDATE funds SET tracked_index_code = '931769' WHERE code = '501060';");
        }
    }
}
