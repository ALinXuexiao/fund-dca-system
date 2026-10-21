using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M13_RemoveDividendSector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 取消「红利」赛道（id=6）：红利低波基金 020602 改归「价值」赛道（id=4），再删除该赛道行。
            // 必须先改基金归属再删赛道，否则违反 funds.sector_id 外键。
            migrationBuilder.Sql("""
                UPDATE funds SET sector_id = 4 WHERE sector_id = 6;
                DELETE FROM sectors WHERE id = 6;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO sectors (id, name, limit_percent, is_equity, sort_order)
                VALUES (6, '红利', 15, true, 60)
                ON CONFLICT (id) DO NOTHING;
                UPDATE funds SET sector_id = 6 WHERE code = '020602';
                """);
        }
    }
}
