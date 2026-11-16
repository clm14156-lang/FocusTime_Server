using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace FocusTime.Api.Infrastructure.Persistence.Migrations;

[DbContext(typeof(AppDbContext))]
[Migration("20261114080000_WebsiteRuleDelta")]
public sealed class WebsiteRuleDelta : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("""
            CREATE TABLE focus.website_rule_changes (
                user_id uuid NOT NULL REFERENCES identity.users("Id") ON DELETE CASCADE,
                rule_id uuid NOT NULL,
                revision bigint NOT NULL CHECK(revision > 0),
                rule jsonb,
                PRIMARY KEY (user_id,rule_id),
                UNIQUE (user_id,revision)
            );
            INSERT INTO focus.website_rule_changes(user_id,rule_id,revision,rule)
            SELECT s.user_id, (r.value->>'id')::uuid, s.revision+r.ordinality, r.value
            FROM focus.website_rule_sets s CROSS JOIN LATERAL jsonb_array_elements(s.rules) WITH ORDINALITY r;
            UPDATE focus.website_rule_sets SET revision=revision+jsonb_array_length(rules);
            """);

    protected override void Down(MigrationBuilder migrationBuilder)
        => migrationBuilder.Sql("DROP TABLE focus.website_rule_changes;");
}
