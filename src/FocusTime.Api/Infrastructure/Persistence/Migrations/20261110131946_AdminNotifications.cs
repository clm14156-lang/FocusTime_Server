using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusTime.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminNotifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "notifications",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    OrderId = table.Column<Guid>(type: "uuid", nullable: true),
                    Service = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MonitorIncidentId = table.Column<Guid>(type: "uuid", nullable: true),
                    Description = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    HandledAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notifications", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "notification_reads",
                schema: "admin",
                columns: table => new
                {
                    AdministratorId = table.Column<Guid>(type: "uuid", nullable: false),
                    NotificationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReadAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_notification_reads", x => new { x.AdministratorId, x.NotificationId });
                    table.ForeignKey(
                        name: "FK_notification_reads_notifications_NotificationId",
                        column: x => x.NotificationId,
                        principalSchema: "admin",
                        principalTable: "notifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_notification_reads_NotificationId",
                schema: "admin",
                table: "notification_reads",
                column: "NotificationId");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_HandledAtUtc",
                schema: "admin",
                table: "notifications",
                column: "HandledAtUtc",
                filter: "\"HandledAtUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_notifications_MonitorIncidentId",
                schema: "admin",
                table: "notifications",
                column: "MonitorIncidentId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_notifications_OccurredAtUtc_Id",
                schema: "admin",
                table: "notifications",
                columns: new[] { "OccurredAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "notification_reads",
                schema: "admin");

            migrationBuilder.DropTable(
                name: "notifications",
                schema: "admin");
        }
    }
}
