using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FocusTime.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261104120000_MembershipOrders")]
public sealed class MembershipOrders : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE membership.grants DROP CONSTRAINT ck_grant_kind_dates;
            ALTER TABLE membership.grants ADD CONSTRAINT ck_grant_kind_dates CHECK (
                ("Kind" = 'lifetime' AND "ExpiresAtUtc" IS NULL) OR
                ("Kind" IN ('monthly', 'annual') AND "ExpiresAtUtc" IS NOT NULL AND "ExpiresAtUtc" > "StartsAtUtc")
            );
            CREATE TABLE membership.orders (
                "Id" uuid CONSTRAINT "PK_orders" PRIMARY KEY,
                "UserId" uuid NOT NULL CONSTRAINT "FK_orders_users_UserId" REFERENCES identity.users("Id") ON DELETE CASCADE,
                "RequestId" uuid NOT NULL,
                "Plan" varchar(16) NOT NULL,
                "AmountFen" integer NOT NULL,
                "Method" varchar(16) NOT NULL,
                "Status" varchar(16) NOT NULL,
                "CreatedAtUtc" timestamptz NOT NULL,
                "ExpiresAtUtc" timestamptz NOT NULL,
                "PaidAtUtc" timestamptz NULL,
                CONSTRAINT ck_order_terms CHECK (
                    "Plan" IN ('monthly', 'annual', 'lifetime') AND "AmountFen" > 0 AND
                    "Method" IN ('wechat', 'alipay') AND "ExpiresAtUtc" > "CreatedAtUtc"
                ),
                CONSTRAINT ck_order_payment CHECK (
                    ("Status" = 'pending' AND "PaidAtUtc" IS NULL) OR
                    ("Status" = 'paid' AND "PaidAtUtc" IS NOT NULL AND "PaidAtUtc" >= "CreatedAtUtc" AND "PaidAtUtc" < "ExpiresAtUtc")
                )
            );
            CREATE UNIQUE INDEX "IX_orders_UserId_RequestId" ON membership.orders ("UserId", "RequestId");
            CREATE INDEX "IX_orders_UserId_CreatedAtUtc" ON membership.orders ("UserId", "CreatedAtUtc");
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP TABLE membership.orders;
            UPDATE membership.grants SET "Kind" = 'annual' WHERE "Kind" = 'monthly';
            ALTER TABLE membership.grants DROP CONSTRAINT ck_grant_kind_dates;
            ALTER TABLE membership.grants ADD CONSTRAINT ck_grant_kind_dates CHECK (
                ("Kind" = 'lifetime' AND "ExpiresAtUtc" IS NULL) OR
                ("Kind" = 'annual' AND "ExpiresAtUtc" IS NOT NULL AND "ExpiresAtUtc" > "StartsAtUtc")
            );
            """);
    }
}
