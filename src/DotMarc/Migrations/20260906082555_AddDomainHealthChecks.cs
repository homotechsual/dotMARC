using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainHealthChecks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DkimCheckDetail",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DkimCheckStatus",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "NotConfigured");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DkimCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "DkimSelectors",
                table: "Domains",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "DmarcAuthorizationCheckDetail",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DmarcAuthorizationCheckStatus",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DmarcAuthorizationCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MxCheckDetail",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MxCheckStatus",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MxCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpfCheckDetail",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SpfCheckStatus",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SpfCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DkimCheckDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DkimCheckStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DkimCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DkimSelectors",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcAuthorizationCheckDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcAuthorizationCheckStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcAuthorizationCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "MxCheckDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "MxCheckStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "MxCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "SpfCheckDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "SpfCheckStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "SpfCheckedUtc",
                table: "Domains");
        }
    }
}
