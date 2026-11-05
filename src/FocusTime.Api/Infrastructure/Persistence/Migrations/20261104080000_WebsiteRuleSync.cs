using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FocusTime.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261104080000_WebsiteRuleSync")]
public sealed class WebsiteRuleSync : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            CREATE TABLE focus.website_rule_sets (
                user_id uuid PRIMARY KEY REFERENCES identity.users("Id") ON DELETE CASCADE,
                revision bigint NOT NULL CHECK (revision > 0),
                rules jsonb NOT NULL CHECK (jsonb_typeof(rules) = 'array')
            );
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("DROP TABLE focus.website_rule_sets;");
}
