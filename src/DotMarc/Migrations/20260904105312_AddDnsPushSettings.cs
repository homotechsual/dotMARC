using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDnsPushSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AzureDnsSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TenantId = table.Column<string>(type: "text", nullable: true),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    ClientSecretConfigured = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AzureDnsSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CloudflareDnsSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClientId = table.Column<string>(type: "text", nullable: true),
                    ClientSecretConfigured = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CloudflareDnsSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AzureDnsSettings",
                columns: new[] { "Id", "ClientId", "ClientSecretConfigured", "TenantId" },
                values: new object[] { 1, null, false, null });

            migrationBuilder.InsertData(
                table: "CloudflareDnsSettings",
                columns: new[] { "Id", "ClientId", "ClientSecretConfigured" },
                values: new object[] { 1, null, false });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AzureDnsSettings");

            migrationBuilder.DropTable(
                name: "CloudflareDnsSettings");
        }
    }
}
