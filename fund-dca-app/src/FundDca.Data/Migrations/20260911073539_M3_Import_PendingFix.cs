using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M3_Import_PendingFix : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "position_versions",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    version = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    file_name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    proof_date = table.Column<DateOnly>(type: "date", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    is_current = table.Column<bool>(type: "boolean", nullable: false),
                    summary = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_versions", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "position_version_items",
                columns: table => new
                {
                    id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    version_id = table.Column<long>(type: "bigint", nullable: false),
                    fund_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    fund_name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    fund_type = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    sector_id = table.Column<int>(type: "integer", nullable: true),
                    is_manual = table.Column<bool>(type: "boolean", nullable: false),
                    shares = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    cost_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: true),
                    market_value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    change_kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_position_version_items", x => x.id);
                    table.ForeignKey(
                        name: "FK_position_version_items_position_versions_version_id",
                        column: x => x.version_id,
                        principalTable: "position_versions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_position_version_items_version_id",
                table: "position_version_items",
                column: "version_id");

            migrationBuilder.CreateIndex(
                name: "IX_position_versions_is_current",
                table: "position_versions",
                column: "is_current");

            migrationBuilder.CreateIndex(
                name: "IX_position_versions_version",
                table: "position_versions",
                column: "version",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "position_version_items");

            migrationBuilder.DropTable(
                name: "position_versions");
        }
    }
}
