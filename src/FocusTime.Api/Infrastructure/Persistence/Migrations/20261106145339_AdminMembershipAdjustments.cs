using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusTime.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminMembershipAdjustments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "admin_adjustments",
                schema: "membership",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OperationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Action = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Plan = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    ReasonCode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    State = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ExpectedFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BeforeJson = table.Column<string>(type: "jsonb", nullable: false),
                    AfterJson = table.Column<string>(type: "jsonb", nullable: false),
                    TargetGrantIdsJson = table.Column<string>(type: "jsonb", nullable: false),
                    GrantId = table.Column<Guid>(type: "uuid", nullable: true),
                    GrantStartsAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    GrantExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AppliedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AppliedVersion = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_admin_adjustments", x => new { x.UserId, x.OperationId });
                    table.ForeignKey(
                        name: "FK_admin_adjustments_users_ActorUserId",
                        column: x => x.ActorUserId,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_admin_adjustments_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_admin_adjustments_ActorUserId",
                schema: "membership",
                table: "admin_adjustments",
                column: "ActorUserId");

            migrationBuilder.CreateIndex(
                name: "IX_admin_adjustments_GrantId",
                schema: "membership",
                table: "admin_adjustments",
                column: "GrantId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admin_adjustments_OperationId",
                schema: "membership",
                table: "admin_adjustments",
                column: "OperationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_admin_adjustments_UserId_AppliedAtUtc",
                schema: "membership",
                table: "admin_adjustments",
                columns: new[] { "UserId", "AppliedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "admin_adjustments",
                schema: "membership");
        }
    }
}
