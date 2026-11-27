using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDmarcTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetDmarcPolicy",
                table: "Groups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDmarcSubdomainPolicy",
                table: "Groups",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDmarcPolicy",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDmarcSubdomainPolicy",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDmarcPolicy",
                table: "DnsRecordSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetDmarcSubdomainPolicy",
                table: "DnsRecordSettings",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetDmarcPolicy",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "TargetDmarcSubdomainPolicy",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "TargetDmarcPolicy",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "TargetDmarcSubdomainPolicy",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "TargetDmarcPolicy",
                table: "DnsRecordSettings");

            migrationBuilder.DropColumn(
                name: "TargetDmarcSubdomainPolicy",
                table: "DnsRecordSettings");
        }
    }
}
