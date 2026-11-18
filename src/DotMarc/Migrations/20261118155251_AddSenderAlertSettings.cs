using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddSenderAlertSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "NewSenderAlertEnabled",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "NewSenderDays",
                table: "NotificationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "NewSenderMinVolume",
                table: "NotificationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<bool>(
                name: "UnauthorisedDeliveredAlertEnabled",
                table: "NotificationSettings",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "UnauthorisedDeliveredMinVolume",
                table: "NotificationSettings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.UpdateData(
                table: "NotificationSettings",
                keyColumn: "Id",
                keyValue: 1,
                columns: new[] { "NewSenderAlertEnabled", "NewSenderDays", "NewSenderMinVolume", "UnauthorisedDeliveredAlertEnabled", "UnauthorisedDeliveredMinVolume" },
                values: new object[] { true, 7, 20, true, 10 });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "NewSenderAlertEnabled",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "NewSenderDays",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "NewSenderMinVolume",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "UnauthorisedDeliveredAlertEnabled",
                table: "NotificationSettings");

            migrationBuilder.DropColumn(
                name: "UnauthorisedDeliveredMinVolume",
                table: "NotificationSettings");
        }
    }
}
