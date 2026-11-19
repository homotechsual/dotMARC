using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddQuietHoursAndEscalation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TimeZoneId",
                table: "NotificationSettings",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "UTC");

            migrationBuilder.AddColumn<int>(
                name: "EscalateAfterMinutes",
                table: "AlertRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuietEndMinutes",
                table: "AlertRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "QuietIgnoredForCritical",
                table: "AlertRules",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "QuietMode",
                table: "AlertRules",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Off");

            migrationBuilder.AddColumn<int>(
                name: "QuietStartMinutes",
                table: "AlertRules",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EscalatedUtc",
                table: "AlertEvents",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "NotifiedUtc",
                table: "AlertEvents",
                type: "timestamp with time zone",
                nullable: true);

            // Alerts raised before this existed were notified when they were created. Counting them so means an upgrade never
            // sends one again, as it would if the column were left empty (an empty NotifiedUtc means a quiet window is holding it).
            migrationBuilder.Sql("UPDATE \"AlertEvents\" SET \"NotifiedUtc\" = \"CreatedUtc\";");

            migrationBuilder.CreateTable(
                name: "AlertRuleEscalationDestinations",
                columns: table => new
                {
                    AlertRuleId = table.Column<int>(type: "integer", nullable: false),
                    AlertDestinationId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRuleEscalationDestinations", x => new { x.AlertRuleId, x.AlertDestinationId });
                    table.ForeignKey(
                        name: "FK_AlertRuleEscalationDestinations_AlertDestinations_AlertDest~",
                        column: x => x.AlertDestinationId,
                        principalTable: "AlertDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertRuleEscalationDestinations_AlertRules_AlertRuleId",
                        column: x => x.AlertRuleId,
                        principalTable: "AlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "NotificationSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "TimeZoneId",
                value: "UTC");

            migrationBuilder.CreateIndex(
                name: "IX_AlertRuleEscalationDestinations_AlertDestinationId",
                table: "AlertRuleEscalationDestinations",
                column: "AlertDestinationId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertRuleEscalationDestinations");

            migrationBuilder.DropColumn(
                name: "TimeZoneId",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "EscalateAfterMinutes",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "QuietEndMinutes",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "QuietIgnoredForCritical",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "QuietMode",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "QuietStartMinutes",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "EscalatedUtc",
                table: "AlertEvents");

            migrationBuilder.DropColumn(
                name: "NotifiedUtc",
                table: "AlertEvents");
        }
    }
}
