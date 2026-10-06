using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddConnectWiseSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ConnectWiseSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    SiteUrl = table.Column<string>(type: "text", nullable: true),
                    CompanyId = table.Column<string>(type: "text", nullable: true),
                    PublicKey = table.Column<string>(type: "text", nullable: true),
                    PrivateKeyConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    ClientIdOverride = table.Column<string>(type: "text", nullable: true),
                    BoardId = table.Column<int>(type: "integer", nullable: true),
                    BoardName = table.Column<string>(type: "text", nullable: true),
                    StatusId = table.Column<int>(type: "integer", nullable: true),
                    StatusName = table.Column<string>(type: "text", nullable: true),
                    TypeId = table.Column<int>(type: "integer", nullable: true),
                    TypeName = table.Column<string>(type: "text", nullable: true),
                    PriorityId = table.Column<int>(type: "integer", nullable: true),
                    PriorityName = table.Column<string>(type: "text", nullable: true),
                    ClosedStatusId = table.Column<int>(type: "integer", nullable: true),
                    ClosedStatusName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConnectWiseSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "ConnectWiseSettings",
                columns: new[] { "Id", "BoardId", "BoardName", "ClientIdOverride", "ClosedStatusId", "ClosedStatusName", "CompanyId", "Enabled", "PriorityId", "PriorityName", "PrivateKeyConfigured", "PublicKey", "SiteUrl", "StatusId", "StatusName", "TypeId", "TypeName" },
                values: new object[] { 1, null, null, null, null, null, null, false, null, null, false, null, null, null, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConnectWiseSettings");
        }
    }
}
