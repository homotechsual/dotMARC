using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddHaloPsaIntegration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HaloClientId",
                table: "Groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HaloClientId",
                table: "Domains",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTicketId",
                table: "AlertEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTicketProvider",
                table: "AlertEvents",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "HaloPsaSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccountName = table.Column<string>(type: "text", nullable: true),
                    AuthServerUrl = table.Column<string>(type: "text", nullable: true),
                    ResourceServerUrl = table.Column<string>(type: "text", nullable: true),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    ClientSecretConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    TicketTypeId = table.Column<int>(type: "integer", nullable: true),
                    DefaultPriorityId = table.Column<int>(type: "integer", nullable: true),
                    ClosedStatusId = table.Column<int>(type: "integer", nullable: true),
                    WebhookSecret = table.Column<string>(type: "text", nullable: true),
                    ProtectedClientSecret = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_HaloPsaSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "HaloPsaSettings",
                columns: new[] { "Id", "AccountName", "AuthServerUrl", "ClientId", "ClientSecretConfigured", "ClosedStatusId", "DefaultPriorityId", "Enabled", "ProtectedClientSecret", "ResourceServerUrl", "TicketTypeId", "WebhookSecret" },
                values: new object[] { 1, null, null, null, false, null, null, false, null, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "HaloPsaSettings");

            migrationBuilder.DropColumn(
                name: "HaloClientId",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "HaloClientId",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "ExternalTicketId",
                table: "AlertEvents");

            migrationBuilder.DropColumn(
                name: "ExternalTicketProvider",
                table: "AlertEvents");
        }
    }
}
