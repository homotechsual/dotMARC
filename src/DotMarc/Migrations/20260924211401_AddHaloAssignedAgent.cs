using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddHaloAssignedAgent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AssignedAgentId",
                table: "HaloPsaSettings",
                type: "integer",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "HaloPsaSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "AssignedAgentId",
                value: null);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedAgentId",
                table: "HaloPsaSettings");
        }
    }
}
