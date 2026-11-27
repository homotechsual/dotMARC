using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDmarcRecordAnalysis : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "DmarcNonExistentSubdomainPolicy",
                table: "Domains",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string[]>(
                name: "DmarcProblems",
                table: "Domains",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<string[]>(
                name: "DmarcRecords",
                table: "Domains",
                type: "text[]",
                nullable: false,
                defaultValue: new string[0]);

            migrationBuilder.AddColumn<bool>(
                name: "DmarcTesting",
                table: "Domains",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DmarcNonExistentSubdomainPolicy",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcProblems",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcRecords",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "DmarcTesting",
                table: "Domains");
        }
    }
}
