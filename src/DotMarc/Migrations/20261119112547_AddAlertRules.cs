using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddAlertRules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AlertRuleId",
                table: "AlertEvents",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AlertRules",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Kind = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Enabled = table.Column<bool>(type: "boolean", nullable: false),
                    Severity = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    StopProcessing = table.Column<bool>(type: "boolean", nullable: false),
                    DefaultKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CooldownMinutes = table.Column<int>(type: "integer", nullable: true),
                    AutoCloseDays = table.Column<int>(type: "integer", nullable: true),
                    ParametersJson = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRules", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AlertRuleScopeEntries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertRuleId = table.Column<int>(type: "integer", nullable: false),
                    Target = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    TargetId = table.Column<int>(type: "integer", nullable: false),
                    Exclude = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertRuleScopeEntries", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertRuleScopeEntries_AlertRules_AlertRuleId",
                        column: x => x.AlertRuleId,
                        principalTable: "AlertRules",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvents_AlertRuleId",
                table: "AlertEvents",
                column: "AlertRuleId");

            migrationBuilder.CreateIndex(
                name: "IX_AlertEvents_DomainName_AlertType_AlertRuleId",
                table: "AlertEvents",
                columns: new[] { "DomainName", "AlertType", "AlertRuleId" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_DefaultKey",
                table: "AlertRules",
                column: "DefaultKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertRules_Kind_Position",
                table: "AlertRules",
                columns: new[] { "Kind", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_AlertRuleScopeEntries_AlertRuleId_Target_TargetId_Exclude",
                table: "AlertRuleScopeEntries",
                columns: new[] { "AlertRuleId", "Target", "TargetId", "Exclude" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_AlertEvents_AlertRules_AlertRuleId",
                table: "AlertEvents",
                column: "AlertRuleId",
                principalTable: "AlertRules",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_AlertEvents_AlertRules_AlertRuleId",
                table: "AlertEvents");

            migrationBuilder.DropTable(
                name: "AlertRuleScopeEntries");

            migrationBuilder.DropTable(
                name: "AlertRules");

            migrationBuilder.DropIndex(
                name: "IX_AlertEvents_AlertRuleId",
                table: "AlertEvents");

            migrationBuilder.DropIndex(
                name: "IX_AlertEvents_DomainName_AlertType_AlertRuleId",
                table: "AlertEvents");

            migrationBuilder.DropColumn(
                name: "AlertRuleId",
                table: "AlertEvents");
        }
    }
}
