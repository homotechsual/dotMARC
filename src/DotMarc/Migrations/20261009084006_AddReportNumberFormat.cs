using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddReportNumberFormat : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "NumberFormat",
                table: "ReportSettings",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "en-GB");

            migrationBuilder.UpdateData(
                table: "ReportSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "NumberFormat",
                value: "en-GB");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NumberFormat",
                table: "ReportSettings");
        }
    }
}
