using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <summary>Replaces the single DeliveryMode setting with a switch per channel (Teams, Slack, generic webhook). The
    /// old mode is copied into the switches before its column goes, so an upgrade keeps sending where it did; Slack
    /// starts off. Hand-edited: the generated version dropped DeliveryMode first and reset the seeded row.</summary>
    public partial class AddAlertChannelSwitches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "TeamsEnabled",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<bool>(
                name: "SlackEnabled",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SlackWebhookUrl",
                table: "NotificationSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "GenericWebhookEnabled",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // "Teams", "Generic" or "Both"; anything else was treated as Teams, so it stays Teams.
            migrationBuilder.Sql(@"UPDATE ""NotificationSettings"" SET
                ""TeamsEnabled"" = ""DeliveryMode"" IS NULL OR ""DeliveryMode"" NOT ILIKE 'Generic',
                ""GenericWebhookEnabled"" = ""DeliveryMode"" ILIKE 'Generic' OR ""DeliveryMode"" ILIKE 'Both';");

            migrationBuilder.DropColumn(
                name: "DeliveryMode",
                table: "NotificationSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DeliveryMode",
                table: "NotificationSettings",
                type: "text",
                nullable: false,
                defaultValue: "Teams");

            // Slack has no equivalent in the old setting, so it's dropped; Teams wins when neither is on.
            migrationBuilder.Sql(@"UPDATE ""NotificationSettings"" SET ""DeliveryMode"" = CASE
                WHEN ""TeamsEnabled"" AND ""GenericWebhookEnabled"" THEN 'Both'
                WHEN ""GenericWebhookEnabled"" THEN 'Generic'
                ELSE 'Teams' END;");

            migrationBuilder.DropColumn(
                name: "GenericWebhookEnabled",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "SlackEnabled",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "SlackWebhookUrl",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "TeamsEnabled",
                table: "NotificationSettings");
        }
    }
}
