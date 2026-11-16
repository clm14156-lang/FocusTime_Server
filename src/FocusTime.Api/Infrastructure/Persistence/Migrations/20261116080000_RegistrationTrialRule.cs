using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FocusTime.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261116080000_RegistrationTrialRule")]
public sealed class RegistrationTrialRule : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE TABLE membership.registration_trial_rule (
                "Id" integer PRIMARY KEY,
                "DurationDays" integer NOT NULL,
                "Version" bigint NOT NULL,
                CONSTRAINT ck_registration_trial_rule CHECK ("Id" = 1 AND "DurationDays" BETWEEN 1 AND 15 AND "Version" > 0)
            );
            INSERT INTO membership.registration_trial_rule ("Id", "DurationDays", "Version") VALUES (1, 7, 1);

            UPDATE membership.plans
            SET "DurationDays" = 30, "Version" = "Version" + 1, "UpdatedAtUtc" = CURRENT_TIMESTAMP, "UpdatedByUserId" = NULL
            WHERE "Id" = 'monthly' AND "DurationDays" <> 30;
            ALTER TABLE membership.plans ADD CONSTRAINT ck_monthly_plan_duration
                CHECK ("Id" <> 'monthly' OR "DurationDays" = 30);
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            ALTER TABLE membership.plans DROP CONSTRAINT ck_monthly_plan_duration;
            DROP TABLE membership.registration_trial_rule;
            """);
    }
}
