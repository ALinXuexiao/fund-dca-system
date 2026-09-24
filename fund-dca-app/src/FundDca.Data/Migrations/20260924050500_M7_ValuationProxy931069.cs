using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M7_ValuationProxy931069 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // 931069 中金300 蛋卷估值未覆盖，配置估值代理指数 000300 沪深300
            // （优选300 为沪深市场 300 只大盘股，与沪深300 高相关；与 930050 的代理口径一致）。
            migrationBuilder.Sql("UPDATE indexes SET proxy_code = '000300' WHERE code = '931069';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE indexes SET proxy_code = NULL WHERE code = '931069';");
        }
    }
}
