using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusTime.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class OrderRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_order_activation",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_payment",
                schema: "membership",
                table: "orders");

            migrationBuilder.CreateTable(
                name: "order_refunds",
                schema: "membership",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AmountFen = table.Column<int>(type: "integer", nullable: false),
                    ReasonCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    RevokeMembership = table.Column<bool>(type: "boolean", nullable: false),
                    RevokedGrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ProviderReference = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_order_refunds", x => x.Id);
                    table.CheckConstraint("ck_refund_receipt", "\"AmountFen\" > 0 AND \"Status\" = 'refunded' AND \"CompletedAtUtc\" >= \"CreatedAtUtc\"");
                    table.ForeignKey(
                        name: "FK_order_refunds_grants_RevokedGrantId",
                        column: x => x.RevokedGrantId,
                        principalSchema: "membership",
                        principalTable: "grants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_refunds_orders_OrderId",
                        column: x => x.OrderId,
                        principalSchema: "membership",
                        principalTable: "orders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_order_refunds_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_activation",
                schema: "membership",
                table: "orders",
                sql: "(\"ActivationStatus\" IN ('notStarted', 'failed') AND \"MembershipGrantId\" IS NULL AND \"ActivatedAtUtc\" IS NULL) OR (\"Status\" IN ('paid', 'refunding', 'refunded') AND \"ActivationStatus\" = 'activated' AND \"MembershipGrantId\" IS NOT NULL AND \"ActivatedAtUtc\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_payment",
                schema: "membership",
                table: "orders",
                sql: "(\"Status\" = 'pending' AND \"PaidAtUtc\" IS NULL) OR (\"Status\" IN ('paid', 'refunding', 'refunded') AND \"PaidAtUtc\" IS NOT NULL AND \"PaidAtUtc\" >= \"CreatedAtUtc\" AND \"PaidAtUtc\" < \"ExpiresAtUtc\")");

            migrationBuilder.CreateIndex(
                name: "IX_order_refunds_ActorUserId",
                schema: "membership",
                table: "order_refunds",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_order_refunds_OrderId",
                schema: "membership",
                table: "order_refunds",
                column: "OrderId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_order_refunds_RevokedGrantId",
                schema: "membership",
                table: "order_refunds",
                column: "RevokedGrantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "order_refunds",
                schema: "membership");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_activation",
                schema: "membership",
                table: "orders");

            migrationBuilder.DropCheckConstraint(
                name: "ck_order_payment",
                schema: "membership",
                table: "orders");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_activation",
                schema: "membership",
                table: "orders",
                sql: "(\"ActivationStatus\" IN ('notStarted', 'failed') AND \"MembershipGrantId\" IS NULL AND \"ActivatedAtUtc\" IS NULL) OR (\"Status\" = 'paid' AND \"ActivationStatus\" = 'activated' AND \"MembershipGrantId\" IS NOT NULL AND \"ActivatedAtUtc\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_order_payment",
                schema: "membership",
                table: "orders",
                sql: "(\"Status\" = 'pending' AND \"PaidAtUtc\" IS NULL) OR (\"Status\" = 'paid' AND \"PaidAtUtc\" IS NOT NULL AND \"PaidAtUtc\" >= \"CreatedAtUtc\" AND \"PaidAtUtc\" < \"ExpiresAtUtc\")");
        }
    }
}
