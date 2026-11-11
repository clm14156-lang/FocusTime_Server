using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace FocusTime.Api.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AdminOperationLogs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "admin");

            migrationBuilder.CreateTable(
                name: "operation_logs",
                schema: "admin",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Administrator = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Category = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Action = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    TargetId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    Target = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Source = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Ip = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ChangesJson = table.Column<string>(type: "jsonb", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_operation_logs", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_operation_logs_Category_Status_OccurredAtUtc",
                schema: "admin",
                table: "operation_logs",
                columns: new[] { "Category", "Status", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_operation_logs_OccurredAtUtc_Id",
                schema: "admin",
                table: "operation_logs",
                columns: new[] { "OccurredAtUtc", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "operation_logs",
                schema: "admin");
        }
    }
}
