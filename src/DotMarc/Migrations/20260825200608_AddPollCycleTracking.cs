using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddPollCycleTracking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PollCycleDailySummaries",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalCycles = table.Column<int>(type: "integer", nullable: false),
                    SuccessfulCycles = table.Column<int>(type: "integer", nullable: false),
                    FailedCycles = table.Column<int>(type: "integer", nullable: false),
                    TotalMessagesChecked = table.Column<int>(type: "integer", nullable: false),
                    TotalReportsParsed = table.Column<int>(type: "integer", nullable: false),
                    TotalParseFailures = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollCycleDailySummaries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PollCycles",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PolledUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    MessagesChecked = table.Column<int>(type: "integer", nullable: false),
                    ReportsParsed = table.Column<int>(type: "integer", nullable: false),
                    ParseFailures = table.Column<int>(type: "integer", nullable: false),
                    Succeeded = table.Column<bool>(type: "boolean", nullable: false),
                    ErrorMessage = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PollCycles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PollCycleDailySummaries_Date",
                table: "PollCycleDailySummaries",
                column: "Date",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PollCycles_PolledUtc",
                table: "PollCycles",
                column: "PolledUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PollCycleDailySummaries");

            migrationBuilder.DropTable(
                name: "PollCycles");
        }
    }
}
