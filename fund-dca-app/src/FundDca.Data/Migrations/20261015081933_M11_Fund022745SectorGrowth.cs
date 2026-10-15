using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M11_Fund022745SectorGrowth : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 022745 赛道修正：中盘(2) → 成长(5)。
            // 中证A500 属成长型策略定位（用户 2026-10-15 确认）；M10 建档时误置为中盘。
            migrationBuilder.Sql("UPDATE funds SET sector_id = 5 WHERE code = '022745';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE funds SET sector_id = 2 WHERE code = '022745';");
        }
    }
}
