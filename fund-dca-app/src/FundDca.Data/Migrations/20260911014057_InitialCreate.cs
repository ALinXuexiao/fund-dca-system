using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "indexes",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    metric = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    low_threshold_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    high_threshold_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    window_years = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_indexes", x => x.code);
                });

            migrationBuilder.CreateTable(
                name: "overlap_groups",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    is_built_in = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_overlap_groups", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "sectors",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    limit_percent = table.Column<decimal>(type: "numeric(5,2)", precision: 5, scale: 2, nullable: false),
                    is_equity = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sectors", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "funds",
                columns: table => new
                {
                    code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    tracked_index_code = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    sector_id = table.Column<int>(type: "integer", nullable: true),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    buy_fee_rate = table.Column<decimal>(type: "numeric(6,4)", precision: 6, scale: 4, nullable: true),
                    redeem_fee_tiers = table.Column<string>(type: "jsonb", nullable: false),
                    dividend_method = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false, defaultValue: "REINVEST")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_funds", x => x.code);
                    table.ForeignKey(
                        name: "FK_funds_indexes_tracked_index_code",
                        column: x => x.tracked_index_code,
                        principalTable: "indexes",
                        principalColumn: "code",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_funds_sectors_sector_id",
                        column: x => x.sector_id,
                        principalTable: "sectors",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "fund_overlaps",
                columns: table => new
                {
                    overlap_group_id = table.Column<int>(type: "integer", nullable: false),
                    fund_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    is_primary = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fund_overlaps", x => new { x.overlap_group_id, x.fund_code });
                    table.ForeignKey(
                        name: "FK_fund_overlaps_funds_fund_code",
                        column: x => x.fund_code,
                        principalTable: "funds",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_fund_overlaps_overlap_groups_overlap_group_id",
                        column: x => x.overlap_group_id,
                        principalTable: "overlap_groups",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_fund_overlaps_fund_code",
                table: "fund_overlaps",
                column: "fund_code");

            migrationBuilder.CreateIndex(
                name: "IX_funds_sector_id",
                table: "funds",
                column: "sector_id");

            migrationBuilder.CreateIndex(
                name: "IX_funds_tracked_index_code",
                table: "funds",
                column: "tracked_index_code");

            migrationBuilder.CreateIndex(
                name: "IX_overlap_groups_name",
                table: "overlap_groups",
                column: "name",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sectors_name",
                table: "sectors",
                column: "name",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "fund_overlaps");

            migrationBuilder.DropTable(
                name: "funds");

            migrationBuilder.DropTable(
                name: "overlap_groups");

            migrationBuilder.DropTable(
                name: "indexes");

            migrationBuilder.DropTable(
                name: "sectors");
        }
    }
}
