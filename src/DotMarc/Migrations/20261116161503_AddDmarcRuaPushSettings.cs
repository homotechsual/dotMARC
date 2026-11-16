using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDmarcRuaPushSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AdditionalRuaAddress",
                table: "DnsRecordSettings",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "KeepOtherRuaAddresses",
                table: "DnsRecordSettings",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            migrationBuilder.UpdateData(
                table: "DnsRecordSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AdditionalRuaAddress", "KeepOtherRuaAddresses" },
                values: new object[] { null, true });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AdditionalRuaAddress",
                table: "DnsRecordSettings");

            migrationBuilder.DropColumn(
                name: "KeepOtherRuaAddresses",
                table: "DnsRecordSettings");
        }
    }
}
