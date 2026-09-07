using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDnsProviderDetection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DnsProvider",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DnsProviderCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DnsZone",
                table: "Domains",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DnsProvider",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DnsProviderCheckedUtc",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DnsZone",
                table: "Domains");
        }
    }
}
