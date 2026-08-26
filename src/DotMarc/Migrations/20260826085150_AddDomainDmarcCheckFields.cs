using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDomainDmarcCheckFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DmarcCheckDetail",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DmarcCheckStatus",
                table: "Domains",
                type: "text",
                nullable: false,
                defaultValue: "NotChecked");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DmarcCheckedUtc",
                table: "Domains",
                type: "timestamp with time zone",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DmarcCheckDetail",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcCheckStatus",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcCheckedUtc",
                table: "Domains");
        }
    }
}
