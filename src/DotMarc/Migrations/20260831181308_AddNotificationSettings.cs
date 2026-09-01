using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "NotificationSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    DeliveryMode = table.Column<string>(type: "text", nullable: false),
                    TeamsWebhookUrl = table.Column<string>(type: "text", nullable: true),
                    GenericWebhookUrl = table.Column<string>(type: "text", nullable: true),
                    MissingReportThresholdDays = table.Column<int>(type: "integer", nullable: false),
                    CooldownMinutes = table.Column<int>(type: "integer", nullable: false),
                    MonitorIntervalSeconds = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_NotificationSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "NotificationSettings",
                columns: new[] { "Id", "CooldownMinutes", "DeliveryMode", "Enabled", "GenericWebhookUrl", "MissingReportThresholdDays", "MonitorIntervalSeconds", "TeamsWebhookUrl" },
                values: new object[] { 1, 180, "Teams", true, null, 2, 300, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "NotificationSettings");
        }
    }
}
