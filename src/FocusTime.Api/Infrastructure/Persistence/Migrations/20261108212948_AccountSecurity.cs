using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusTime.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AccountSecurity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ClosedAtUtc",
                schema: "identity",
                table: "users",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "security_audits",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Operation = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Result = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_audits", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "security_challenges",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    SessionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Purpose = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    StampHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ParentId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    Consumed = table.Column<bool>(type: "boolean", nullable: false),
                    ProofHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ProofExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProofConsumed = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_challenges", x => x.Id);
                    table.ForeignKey(
                        name: "FK_security_challenges_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "security_notifications",
                schema: "identity",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Recipient = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Subject = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Delivered = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_security_notifications", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_security_audits_UserId_OccurredAtUtc",
                schema: "identity",
                table: "security_audits",
                columns: new[] { "UserId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_security_challenges_Email_CreatedAtUtc",
                schema: "identity",
                table: "security_challenges",
                columns: new[] { "Email", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_security_challenges_ProofHash",
                schema: "identity",
                table: "security_challenges",
                column: "ProofHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_security_challenges_UserId_Purpose_CreatedAtUtc",
                schema: "identity",
                table: "security_challenges",
                columns: new[] { "UserId", "Purpose", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_security_notifications_CompletedAtUtc_NextAttemptAtUtc",
                schema: "identity",
                table: "security_notifications",
                columns: new[] { "CompletedAtUtc", "NextAttemptAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "security_audits",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "security_challenges",
                schema: "identity");

            migrationBuilder.DropTable(
                name: "security_notifications",
                schema: "identity");

            migrationBuilder.DropColumn(
                name: "ClosedAtUtc",
                schema: "identity",
                table: "users");
        }
    }
}
