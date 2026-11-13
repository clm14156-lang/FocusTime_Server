using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FocusTime.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261113000000_LoginProtection")]
public sealed class LoginProtection : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            CREATE TABLE identity.login_attempts (
                "AccountKey" varchar(64) PRIMARY KEY,
                "WindowStartedAtUtc" timestamptz NOT NULL,
                "RequestCount" integer NOT NULL,
                "ConsecutiveFailures" integer NOT NULL,
                "LastFailureAtUtc" timestamptz NULL
            );
            CREATE INDEX "IX_login_attempts_WindowStartedAtUtc" ON identity.login_attempts ("WindowStartedAtUtc");

            CREATE TABLE identity.login_captchas (
                "AccountKey" varchar(64) NOT NULL REFERENCES identity.login_attempts ("AccountKey") ON DELETE CASCADE,
                "InstallationId" uuid NOT NULL,
                "ChallengeId" uuid NOT NULL,
                "AnswerHash" varchar(64) NOT NULL,
                "ExpiresAtUtc" timestamptz NOT NULL,
                PRIMARY KEY ("AccountKey", "InstallationId")
            );
            CREATE INDEX "IX_login_captchas_ExpiresAtUtc" ON identity.login_captchas ("ExpiresAtUtc");
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("DROP TABLE identity.login_captchas; DROP TABLE identity.login_attempts;");
}
