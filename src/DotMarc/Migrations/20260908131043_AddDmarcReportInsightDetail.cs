using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDmarcReportInsightDetail : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "AuthDetailBackfilledUtc",
                table: "Reports",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ReportRecordAuthDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReportRecordId = table.Column<int>(type: "integer", nullable: false),
                    Mechanism = table.Column<string>(type: "text", nullable: false),
                    Domain = table.Column<string>(type: "text", nullable: false),
                    Result = table.Column<string>(type: "text", nullable: false),
                    Selector = table.Column<string>(type: "text", nullable: true),
                    Scope = table.Column<string>(type: "text", nullable: true),
                    HumanResult = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRecordAuthDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportRecordAuthDetails_ReportRecords_ReportRecordId",
                        column: x => x.ReportRecordId,
                        principalTable: "ReportRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ReportRecordPolicyOverrideReasons",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ReportRecordId = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    Comment = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ReportRecordPolicyOverrideReasons", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ReportRecordPolicyOverrideReasons_ReportRecords_ReportRecor~",
                        column: x => x.ReportRecordId,
                        principalTable: "ReportRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ReportRecordAuthDetails_ReportRecordId",
                table: "ReportRecordAuthDetails",
                column: "ReportRecordId");

            migrationBuilder.CreateIndex(
                name: "IX_ReportRecordPolicyOverrideReasons_ReportRecordId",
                table: "ReportRecordPolicyOverrideReasons",
                column: "ReportRecordId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ReportRecordAuthDetails");

            migrationBuilder.DropTable(
                name: "ReportRecordPolicyOverrideReasons");

            migrationBuilder.DropColumn(
                name: "AuthDetailBackfilledUtc",
                table: "Reports");
        }
    }
}
