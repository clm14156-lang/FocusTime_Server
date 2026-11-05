using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusTime.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderFulfillment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ActivatedAtUtc",
                schema: "membership",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ActivationAttemptedAtUtc",
                schema: "membership",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivationErrorCode",
                schema: "membership",
                table: "orders",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivationStatus",
                schema: "membership",
                table: "orders",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "notStarted");

            migrationBuilder.AddColumn<Guid>(
                name: "MembershipGrantId",
                schema: "membership",
                table: "orders",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentProvider",
                schema: "membership",
                table: "orders",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PaymentReference",
                schema: "membership",
                table: "orders",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "order_events",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Actor = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_events", x => x.Id);
                    table.ForeignKey(
                        name: "FK_order_events_orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "membership",
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            // Recover fulfillment from existing grants; do not treat a paid order as activated without evidence.
            migrationBuilder.Sql("""
                UPDATE membership.orders o SET "ActivationStatus" = 'activated', "MembershipGrantId" = g."Id",
                    "ActivatedAtUtc" = g."CreatedAtUtc", "ActivationAttemptedAtUtc" = g."CreatedAtUtc"
                FROM membership.grants g WHERE o."Status" = 'paid' AND g."Id" = o."Id"
                    AND g."UserId" = o."UserId" AND g."Kind" = o."Plan";
                UPDATE membership.orders SET "ActivationStatus" = 'failed', "ActivationAttemptedAtUtc" = "PaidAtUtc",
                    "ActivationErrorCode" = 'legacy_membership_missing'
                WHERE "Status" = 'paid' AND "ActivationStatus" <> 'activated';
                UPDATE membership.orders SET "PaymentProvider" = 'simulation',
                    "PaymentReference" = 'SIM' || replace("Id"::text, '-', '') WHERE "Status" = 'paid';
                INSERT INTO membership.order_events ("Id", "OrderId", "Kind", "Actor", "OccurredAtUtc")
                    SELECT gen_random_uuid(), "Id", 'order_created', 'migration', "CreatedAtUtc" FROM membership.orders;
                INSERT INTO membership.order_events ("Id", "OrderId", "Kind", "Actor", "OccurredAtUtc")
                    SELECT gen_random_uuid(), "Id", 'payment_confirmed', 'migration', "PaidAtUtc"
                    FROM membership.orders WHERE "Status" = 'paid';
                INSERT INTO membership.order_events ("Id", "OrderId", "Kind", "Actor", "OccurredAtUtc")
                    SELECT gen_random_uuid(), "Id", CASE WHEN "ActivationStatus" = 'activated'
                        THEN 'membership_activated' ELSE 'membership_activation_failed' END, 'migration', "ActivationAttemptedAtUtc"
                    FROM membership.orders WHERE "Status" = 'paid';
                """);

            migrationBuilder.CreateIndex(
                name: "IX_orders_MembershipGrantId",
                schema: "membership",
                table: "orders",
                column: "MembershipGrantId");

            migrationBuilder.CreateIndex(
                name: "IX_orders_PaymentProvider_PaymentReference",
                schema: "membership",
                table: "orders",
                columns: new[] { "PaymentProvider", "PaymentReference" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_orders_Status_ActivationStatus_CreatedAtUtc",
                schema: "membership",
                table: "orders",
                columns: new[] { "Status", "ActivationStatus", "CreatedAtUtc" });

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_activation",
                schema: "membership",
                table: "orders",
                sql: "(\"ActivationStatus\" IN ('notStarted', 'failed') AND \"MembershipGrantId\" IS NULL AND \"ActivatedAtUtc\" IS NULL) OR (\"Status\" = 'paid' AND \"ActivationStatus\" = 'activated' AND \"MembershipGrantId\" IS NOT NULL AND \"ActivatedAtUtc\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_order_events_OrderId_OccurredAtUtc",
                schema: "membership",
                table: "order_events",
                columns: new[] { "OrderId", "OccurredAtUtc" });

            migrationBuilder.AddForeignKey(
                name: "FK_orders_grants_MembershipGrantId",
                schema: "membership",
                table: "orders",
                column: "MembershipGrantId",
                principalSchema: "membership",
                principalTable: "grants",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_orders_grants_MembershipGrantId",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropTable(
                name: "order_events",
                schema: "membership");

            migrationBuilder.DropIndex(
                name: "IX_orders_MembershipGrantId",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_PaymentProvider_PaymentReference",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropIndex(
                name: "IX_orders_Status_ActivationStatus_CreatedAtUtc",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_activation",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ActivatedAtUtc",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ActivationAttemptedAtUtc",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ActivationErrorCode",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "ActivationStatus",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "MembershipGrantId",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "PaymentProvider",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "PaymentReference",
                schema: "membership",
                table: "orders");
        }
    }
}
