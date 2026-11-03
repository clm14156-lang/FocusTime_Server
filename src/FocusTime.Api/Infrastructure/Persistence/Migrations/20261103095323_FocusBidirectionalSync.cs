using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusTime.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class FocusBidirectionalSync : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "changes",
                schema: "focus",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    RecordId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceDeviceId = table.Column<Guid>(type: "uuid", nullable: false),
                    IsDeleted = table.Column<bool>(type: "boolean", nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    ReceivedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_changes", x => new { x.UserId, x.Revision });
                    table.ForeignKey(
                        name: "FK_changes_users_UserId",
                        column: x => x.UserId,
                        principalSchema: "identity",
                        principalTable: "users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_changes_UserId_RecordId_Revision",
                schema: "focus",
                table: "changes",
                columns: new[] { "UserId", "RecordId", "Revision" });

            migrationBuilder.Sql("""
                INSERT INTO focus.changes ("UserId", "Revision", "RecordId", "SourceDeviceId", "IsDeleted", "PayloadJson", "ReceivedAtUtc")
                SELECT "UserId", "Revision", "RecordId", "SourceDeviceId", "IsDeleted", "PayloadJson", "ReceivedAtUtc"
                FROM focus.records;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "changes",
                schema: "focus");
        }
    }
}
