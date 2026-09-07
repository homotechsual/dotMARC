using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class RemoveAzureDnsTenantId : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TenantId",
                table: "AzureDnsSettings");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TenantId",
                table: "AzureDnsSettings",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "AzureDnsSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "TenantId",
                value: null);
        }
    }
}
