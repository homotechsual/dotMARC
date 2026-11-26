using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDmarcReportDestinationOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdditionalRuaAddress",
                table: "Groups",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OverrideAdditionalRuaAddress",
                table: "Groups",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "AdditionalRuaAddress",
                table: "Domains",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "OverrideAdditionalRuaAddress",
                table: "Domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdditionalRuaAddress",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "OverrideAdditionalRuaAddress",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "AdditionalRuaAddress",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "OverrideAdditionalRuaAddress",
                table: "Domains");
        }
    }
}
