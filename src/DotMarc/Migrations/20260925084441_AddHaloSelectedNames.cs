using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddHaloSelectedNames : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AssignedAgentName",
                table: "HaloPsaSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClosedStatusName",
                table: "HaloPsaSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "DefaultPriorityName",
                table: "HaloPsaSettings",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TicketTypeName",
                table: "HaloPsaSettings",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "HaloPsaSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "AssignedAgentName", "ClosedStatusName", "DefaultPriorityName", "TicketTypeName" },
                values: new object[] { null, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AssignedAgentName",
                table: "HaloPsaSettings");

            migrationBuilder.DropColumn(
                name: "ClosedStatusName",
                table: "HaloPsaSettings");

            migrationBuilder.DropColumn(
                name: "DefaultPriorityName",
                table: "HaloPsaSettings");

            migrationBuilder.DropColumn(
                name: "TicketTypeName",
                table: "HaloPsaSettings");
        }
    }
}
