using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M14_RenameTechSector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 赛道改名：港股科技 → 科技（id=8，仅改名，基金归属与占比口径不变）。
            migrationBuilder.Sql("UPDATE sectors SET name = '科技' WHERE id = 8;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE sectors SET name = '港股科技' WHERE id = 8;");
        }
    }
}
