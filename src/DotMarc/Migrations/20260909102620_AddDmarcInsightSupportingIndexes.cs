using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDmarcInsightSupportingIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Reports_AuthDetailBackfilledUtc",
                table: "Reports",
                column: "AuthDetailBackfilledUtc",
                filter: "\"AuthDetailBackfilledUtc\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRecords_SourceIp",
                table: "ReportRecords",
                column: "SourceIp");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Reports_AuthDetailBackfilledUtc",
                table: "Reports");

            migrationBuilder.DropIndex(
                name: "IX_ReportRecords_SourceIp",
                table: "ReportRecords");
        }
    }
}
