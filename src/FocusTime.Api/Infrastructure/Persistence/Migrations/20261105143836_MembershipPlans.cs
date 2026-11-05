using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusTime.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class MembershipPlans : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DurationDays",
                schema: "membership",
                table: "orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "DurationMonths",
                schema: "membership",
                table: "orders",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlanName",
                schema: "membership",
                table: "orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<long>(
                name: "PlanVersion",
                schema: "membership",
                table: "orders",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "plans",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Name = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AmountFen = table.Column<int>(type: "integer", nullable: false),
                    DurationDays = table.Column<int>(type: "integer", nullable: true),
                    Available = table.Column<bool>(type: "boolean", nullable: false),
                    Recommended = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedByUserId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_plans", x => x.Id);
                    table.CheckConstraint("ck_plan_terms", "\"Id\" IN ('monthly', 'annual', 'lifetime') AND \"AmountFen\" BETWEEN 1 AND 100000000 AND \"Version\" > 0 AND ((\"Id\" = 'lifetime' AND \"DurationDays\" IS NULL) OR (\"Id\" <> 'lifetime' AND \"DurationDays\" IS NOT NULL AND \"DurationDays\" BETWEEN 1 AND 36500))");
                    table.ForeignKey(
                        name: "FK_plans_users_UpdatedByUserId",
                        column: x => x.UpdatedByUserId,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql("""
                INSERT INTO membership.plans ("Id", "Name", "AmountFen", "DurationDays", "Available", "Recommended", "Version", "UpdatedAtUtc")
                VALUES ('monthly', '月会员', 1500, 30, true, false, 1, CURRENT_TIMESTAMP),
                       ('annual', '年会员', 9800, 365, true, true, 1, CURRENT_TIMESTAMP),
                       ('lifetime', '永久会员', 19800, NULL, true, false, 1, CURRENT_TIMESTAMP);
                UPDATE membership.orders SET "PlanName" = CASE "Plan" WHEN 'monthly' THEN '月会员'
                    WHEN 'annual' THEN '年会员' ELSE '永久会员' END,
                    "DurationMonths" = CASE "Plan" WHEN 'monthly' THEN 1 WHEN 'annual' THEN 12 ELSE NULL END;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_orders_Plan",
                schema: "membership",
                table: "orders",
                column: "Plan");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_duration_snapshot",
                schema: "membership",
                table: "orders",
                sql: "(\"Plan\" = 'lifetime' AND \"DurationDays\" IS NULL AND \"DurationMonths\" IS NULL) OR (\"Plan\" <> 'lifetime' AND ((\"DurationDays\" > 0 AND \"DurationDays\" IS NOT NULL AND \"DurationMonths\" IS NULL) OR (\"DurationMonths\" > 0 AND \"DurationMonths\" IS NOT NULL AND \"DurationDays\" IS NULL)))");

            migrationBuilder.CreateIndex(
                name: "IX_plans_UpdatedByUserId",
                schema: "membership",
                table: "plans",
                column: "UpdatedByUserId");

            migrationBuilder.AddForeignKey(
                name: "FK_orders_plans_Plan",
                schema: "membership",
                table: "orders",
                column: "Plan",
                principalSchema: "membership",
                principalTable: "plans",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_orders_plans_Plan",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropTable(
                name: "plans",
                schema: "membership");

            migrationBuilder.DropIndex(
                name: "IX_orders_Plan",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_duration_snapshot",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DurationDays",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "DurationMonths",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "PlanName",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "PlanVersion",
                schema: "membership",
                table: "orders");
        }
    }
}
