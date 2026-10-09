using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M9_EarningsYield931069 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 931069 中金300 估值口径由 PE 历史百分位改为盈利收益率（E/P = 100 / PE-TTM，
            // 越高越便宜），低估线/高估线与 H30269 中证红利低波动保持一致：≥10% 绿灯、≤6.4% 红灯。
            // 历史 index_valuations 的 pe_ttm 序列无需清理——E/P 由 PE 现算，百分位列在该口径下不展示。
            migrationBuilder.Sql(@"
                UPDATE indexes
                   SET metric = 'EarningsYield',
                       low_threshold_percent = 10.00,
                       high_threshold_percent = 6.40
                 WHERE code = '931069';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
                UPDATE indexes
                   SET metric = 'PeTtm',
                       low_threshold_percent = 30.00,
                       high_threshold_percent = 70.00
                 WHERE code = '931069';");
        }
    }
}
