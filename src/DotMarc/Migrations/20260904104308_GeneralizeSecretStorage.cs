using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class GeneralizeSecretStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ProtectedClientSecret",
                table: "HaloPsaSettings");

            migrationBuilder.CreateTable(
                name: "EncryptedSecrets",
                columns: table => new
                {
                    Key = table.Column<string>(type: "text", nullable: false),
                    ProtectedValue = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EncryptedSecrets", x => x.Key);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EncryptedSecrets");

            migrationBuilder.AddColumn<string>(
                name: "ProtectedClientSecret",
                table: "HaloPsaSettings",
                type: "text",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "HaloPsaSettings",
                keyColumn: "Id",
                keyValue: 1,
                column: "ProtectedClientSecret",
                value: null);
        }
    }
}
