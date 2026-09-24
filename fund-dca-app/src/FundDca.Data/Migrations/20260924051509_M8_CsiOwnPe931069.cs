using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M8_CsiOwnPe931069 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 撤销沪深300估值代理：931069 改由中证官网自有 PE 历史计算百分位（本指数自己的估值水平）
            migrationBuilder.Sql("UPDATE indexes SET proxy_code = NULL WHERE code = '931069';");

            // 清理错误口径的历史估值：M7 按沪深300写入的代理行，以及 M6 从 931769 迁来的
            // 全指医药代理行；下次刷新即按 931069 自有 PE 重新写入。
            migrationBuilder.Sql("DELETE FROM index_valuations WHERE index_code = '931069';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE indexes SET proxy_code = '000300' WHERE code = '931069';");
        }
    }
}
