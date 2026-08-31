using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainMtaStsFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "MtaStsCheckDetail",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MtaStsCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "MtaStsEnabled",
                table: "Domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "MtaStsMaxAgeSeconds",
                table: "Domains",
                type: "integer",
                nullable: false,
                defaultValue: 604800);

            migrationBuilder.AddColumn<string>(
                name: "MtaStsMode",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "Testing");

            migrationBuilder.AddColumn<string[]>(
                name: "MtaStsMxHosts",
                table: "Domains",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string>(
                name: "MtaStsStatus",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "NotConfigured");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MtaStsCheckDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "MtaStsCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "MtaStsEnabled",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "MtaStsMaxAgeSeconds",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "MtaStsMode",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "MtaStsMxHosts",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "MtaStsStatus",
                table: "Domains");
        }
    }
}
