using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddAutotaskSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AutotaskSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Username = table.Column<string>(type: "text", nullable: true),
                    SecretConfigured = table.Column<bool>(type: "boolean", nullable: false),
                    IntegrationCodeOverride = table.Column<string>(type: "text", nullable: true),
                    QueueId = table.Column<int>(type: "integer", nullable: true),
                    QueueName = table.Column<string>(type: "text", nullable: true),
                    TicketTypeId = table.Column<int>(type: "integer", nullable: true),
                    TicketTypeName = table.Column<string>(type: "text", nullable: true),
                    IssueTypeId = table.Column<int>(type: "integer", nullable: true),
                    IssueTypeName = table.Column<string>(type: "text", nullable: true),
                    PriorityId = table.Column<int>(type: "integer", nullable: true),
                    PriorityName = table.Column<string>(type: "text", nullable: true),
                    ClosedStatusId = table.Column<int>(type: "integer", nullable: true),
                    ClosedStatusName = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutotaskSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "AutotaskSettings",
                columns: new[] { "Id", "ClosedStatusId", "ClosedStatusName", "Enabled", "IntegrationCodeOverride", "IssueTypeId", "IssueTypeName", "PriorityId", "PriorityName", "QueueId", "QueueName", "SecretConfigured", "TicketTypeId", "TicketTypeName", "Username" },
                values: new object[] { 1, 5, "Complete", false, null, null, null, null, null, null, null, false, null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutotaskSettings");
        }
    }
}
