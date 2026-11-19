using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertDestinations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DestinationMode",
                table: "AlertRules",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "All");

            migrationBuilder.AddColumn<string>(
                name: "TicketMode",
                table: "AlertRules",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "FollowRules");

            migrationBuilder.CreateTable(
                name: "AlertDestinations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Url = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertDestinations", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertRuleDestinations",
                columns: table => new
                {
                    AlertRuleId = table.Column<int>(type: "integer", nullable: false),
                    AlertDestinationId = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRuleDestinations", x => new { x.AlertRuleId, x.AlertDestinationId });
                    table.ForeignKey(
                        name: "FK_AlertRuleDestinations_AlertDestinations_AlertDestinationId",
                        column: x => x.AlertDestinationId,
                        principalTable: "AlertDestinations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AlertRuleDestinations_AlertRules_AlertRuleId",
                        column: x => x.AlertRuleId,
                        principalTable: "AlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRuleDestinations_AlertDestinationId",
                table: "AlertRuleDestinations",
                column: "AlertDestinationId");

            // Names are unique ignoring case, the same way API key names are.
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ""IX_AlertDestinations_LowerName"" ON ""AlertDestinations"" (lower(""Name""));");

            // The three fixed channels become destinations, so an upgraded install sends to the same places. A channel with no
            // URL had nothing to send to and creates none. The old settings columns stay for one release and are no longer read.
            migrationBuilder.Sql(@"
                INSERT INTO ""AlertDestinations"" (""Name"", ""Kind"", ""Url"", ""Enabled"", ""CreatedUtc"")
                SELECT 'Teams', 'Teams', ""TeamsWebhookUrl"", ""TeamsEnabled"", now()
                FROM ""NotificationSettings"" WHERE btrim(coalesce(""TeamsWebhookUrl"", '')) <> '';
                INSERT INTO ""AlertDestinations"" (""Name"", ""Kind"", ""Url"", ""Enabled"", ""CreatedUtc"")
                SELECT 'Slack', 'Slack', ""SlackWebhookUrl"", ""SlackEnabled"", now()
                FROM ""NotificationSettings"" WHERE btrim(coalesce(""SlackWebhookUrl"", '')) <> '';
                INSERT INTO ""AlertDestinations"" (""Name"", ""Kind"", ""Url"", ""Enabled"", ""CreatedUtc"")
                SELECT 'Generic webhook', 'GenericWebhook', ""GenericWebhookUrl"", ""GenericWebhookEnabled"", now()
                FROM ""NotificationSettings"" WHERE btrim(coalesce(""GenericWebhookUrl"", '')) <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertRuleDestinations");

            migrationBuilder.DropTable(
                name: "AlertDestinations");

            migrationBuilder.DropColumn(
                name: "DestinationMode",
                table: "AlertRules");

            migrationBuilder.DropColumn(
                name: "TicketMode",
                table: "AlertRules");
        }
    }
}
