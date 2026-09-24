using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FundDca.Data.Migrations
{
    /// <inheritdoc />
    public partial class M1_PortfolioNavSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "sort_order",
                table: "sectors",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "budget_months",
                columns: table => new
                {
                    year_month = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    budget_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    invested_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    bond_sweep_done = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_budget_months", x => x.year_month);
                });

            migrationBuilder.CreateTable(
                name: "daily_snapshots",
                columns: table => new
                {
                    fund_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    trade_date = table.Column<DateOnly>(type: "date", nullable: false),
                    shares = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    unit_nav = table.Column<decimal>(type: "numeric(12,5)", precision: 12, scale: 5, nullable: false),
                    market_value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    day_change_percent = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_daily_snapshots", x => new { x.fund_code, x.trade_date });
                    table.ForeignKey(
                        name: "FK_daily_snapshots_funds_fund_code",
                        column: x => x.fund_code,
                        principalTable: "funds",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "fund_navs",
                columns: table => new
                {
                    fund_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    trade_date = table.Column<DateOnly>(type: "date", nullable: false),
                    unit_nav = table.Column<decimal>(type: "numeric(12,5)", precision: 12, scale: 5, nullable: false),
                    acc_nav = table.Column<decimal>(type: "numeric(12,5)", precision: 12, scale: 5, nullable: true),
                    day_change_percent = table.Column<decimal>(type: "numeric(8,4)", precision: 8, scale: 4, nullable: true),
                    source = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    fetched_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_fund_navs", x => new { x.fund_code, x.trade_date });
                    table.ForeignKey(
                        name: "FK_fund_navs_funds_fund_code",
                        column: x => x.fund_code,
                        principalTable: "funds",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "holdings",
                columns: table => new
                {
                    fund_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    shares = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    cost_amount = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    opened_at = table.Column<DateOnly>(type: "date", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_holdings", x => x.fund_code);
                    table.ForeignKey(
                        name: "FK_holdings_funds_fund_code",
                        column: x => x.fund_code,
                        principalTable: "funds",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "manual_values",
                columns: table => new
                {
                    fund_code = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    market_value = table.Column<decimal>(type: "numeric(18,4)", precision: 18, scale: 4, nullable: false),
                    value_date = table.Column<DateOnly>(type: "date", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_manual_values", x => x.fund_code);
                    table.ForeignKey(
                        name: "FK_manual_values_funds_fund_code",
                        column: x => x.fund_code,
                        principalTable: "funds",
                        principalColumn: "code",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_daily_snapshots_trade_date",
                table: "daily_snapshots",
                column: "trade_date");

            migrationBuilder.CreateIndex(
                name: "IX_fund_navs_fund_code",
                table: "fund_navs",
                column: "fund_code");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "budget_months");

            migrationBuilder.DropTable(
                name: "daily_snapshots");

            migrationBuilder.DropTable(
                name: "fund_navs");

            migrationBuilder.DropTable(
                name: "holdings");

            migrationBuilder.DropTable(
                name: "manual_values");

            migrationBuilder.DropColumn(
                name: "sort_order",
                table: "sectors");
        }
    }
}
