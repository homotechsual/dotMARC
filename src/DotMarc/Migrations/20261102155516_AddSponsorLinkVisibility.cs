using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddSponsorLinkVisibility : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ShowSponsorLink",
                table: "BrandingSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.UpdateData(
                table: "BrandingSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "ShowSponsorLink",
                value: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ShowSponsorLink",
                table: "BrandingSettings");
        }
    }
}
