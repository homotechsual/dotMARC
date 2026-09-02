using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainTlsrptCheckFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TlsrptCheckDetail",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TlsrptCheckStatus",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "TlsrptCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TlsrptCheckDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "TlsrptCheckStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "TlsrptCheckedUtc",
                table: "Domains");
        }
    }
}
