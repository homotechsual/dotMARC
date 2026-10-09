using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "EmailSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Provider = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    FromAddress = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    FromName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    SmtpHost = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: true),
                    SmtpPort = table.Column<int>(type: "integer", nullable: false),
                    SmtpSecurity = table.Column<string>(type: "character varying(15)", maxLength: 15, nullable: false),
                    SmtpUsername = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    SmtpPasswordConfigured = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EmailSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "EmailSettings",
                columns: new[] { "Id", "FromAddress", "FromName", "Provider", "SmtpHost", "SmtpPasswordConfigured", "SmtpPort", "SmtpSecurity", "SmtpUsername" },
                values: new object[] { 1, null, null, "Off", null, false, 587, "StartTls", null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "EmailSettings");
        }
    }
}
