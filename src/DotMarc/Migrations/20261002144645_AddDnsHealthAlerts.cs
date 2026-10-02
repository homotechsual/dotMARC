using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDnsHealthAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AcknowledgeableAutoCloseDays",
                table: "NotificationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "DkimAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<string>(
                name: "DmarcAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<string>(
                name: "DmarcAuthorizationAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<bool>(
                name: "DmarcPolicyWeakenedEnabled",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "MtaStsAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<string>(
                name: "MxAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<bool>(
                name: "NameserversChangedEnabled",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "SpfAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<string>(
                name: "TlsrptAlertMode",
                table: "NotificationSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "WhenItBreaks");

            migrationBuilder.AddColumn<int>(
                name: "DmarcPercent",
                table: "Domains",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DmarcPolicy",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DmarcSubdomainPolicy",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "DomainAlertStates",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DomainId = table.Column<int>(type: "integer", nullable: false),
                    Item = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    HasPassed = table.Column<bool>(type: "boolean", nullable: false),
                    Baseline = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    PendingSinceUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RecheckDueUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DomainAlertStates", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DomainAlertStates_Domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "Domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.UpdateData(
                table: "NotificationSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AcknowledgeableAutoCloseDays", "DkimAlertMode", "DmarcAlertMode", "DmarcAuthorizationAlertMode", "DmarcPolicyWeakenedEnabled", "MtaStsAlertMode", "MxAlertMode", "NameserversChangedEnabled", "SpfAlertMode", "TlsrptAlertMode" },
                values: new object[] { 0, "WhenItBreaks", "WhenItBreaks", "WhenItBreaks", true, "WhenItBreaks", "WhenItBreaks", true, "WhenItBreaks", "WhenItBreaks" });

            migrationBuilder.CreateIndex(
                name: "IX_DomainAlertStates_DomainId_Item",
                table: "DomainAlertStates",
                columns: new[] { "DomainId", "Item" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DomainAlertStates");

            migrationBuilder.DropColumn(
                name: "AcknowledgeableAutoCloseDays",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "DkimAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "DmarcAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "DmarcAuthorizationAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "DmarcPolicyWeakenedEnabled",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "MtaStsAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "MxAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "NameserversChangedEnabled",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "SpfAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "TlsrptAlertMode",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "DmarcPercent",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcPolicy",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcSubdomainPolicy",
                table: "Domains");
        }
    }
}
