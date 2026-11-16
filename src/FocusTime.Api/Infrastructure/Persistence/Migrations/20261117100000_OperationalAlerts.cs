using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FocusTime.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261117100000_OperationalAlerts")]
public sealed class OperationalAlerts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        ALTER TABLE admin.notifications
            ADD COLUMN "AlertKey" varchar(160),
            ADD COLUMN "LastOccurredAtUtc" timestamptz,
            ADD COLUMN "OccurrenceCount" integer NOT NULL DEFAULT 1,
            ADD COLUMN "Resolution" varchar(24),
            ADD COLUMN "AuditId" varchar(64);
        CREATE UNIQUE INDEX "IX_notifications_AlertKey" ON admin.notifications ("AlertKey")
            WHERE "AlertKey" IS NOT NULL AND "HandledAtUtc" IS NULL;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        DROP INDEX admin."IX_notifications_AlertKey";
        ALTER TABLE admin.notifications DROP COLUMN "AlertKey", DROP COLUMN "LastOccurredAtUtc",
            DROP COLUMN "OccurrenceCount", DROP COLUMN "Resolution", DROP COLUMN "AuditId";
        """);
}
