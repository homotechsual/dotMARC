using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddTlsrptReportStorage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "TlsrptReports",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DomainId = table.Column<int>(type: "integer", nullable: false),
                    ReportingOrg = table.Column<string>(type: "text", nullable: false),
                    ReportId = table.Column<string>(type: "text", nullable: false),
                    DateRangeBeginUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DateRangeEndUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RawJson = table.Column<string>(type: "text", nullable: false),
                    ReceivedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TlsrptReports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TlsrptReports_Domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "Domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TlsrptReportPolicies",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TlsrptReportId = table.Column<int>(type: "integer", nullable: false),
                    PolicyType = table.Column<string>(type: "text", nullable: false),
                    PolicyDomain = table.Column<string>(type: "text", nullable: false),
                    SuccessfulSessionCount = table.Column<long>(type: "bigint", nullable: false),
                    FailedSessionCount = table.Column<long>(type: "bigint", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TlsrptReportPolicies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TlsrptReportPolicies_TlsrptReports_TlsrptReportId",
                        column: x => x.TlsrptReportId,
                        principalTable: "TlsrptReports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TlsrptFailureDetails",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    TlsrptReportPolicyId = table.Column<int>(type: "integer", nullable: false),
                    ResultType = table.Column<string>(type: "text", nullable: false),
                    FailedSessionCount = table.Column<long>(type: "bigint", nullable: false),
                    ReceivingMxHostname = table.Column<string>(type: "text", nullable: true),
                    FailureReasonCode = table.Column<string>(type: "text", nullable: true),
                    AdditionalInformation = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TlsrptFailureDetails", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TlsrptFailureDetails_TlsrptReportPolicies_TlsrptReportPolic~",
                        column: x => x.TlsrptReportPolicyId,
                        principalTable: "TlsrptReportPolicies",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TlsrptFailureDetails_TlsrptReportPolicyId",
                table: "TlsrptFailureDetails",
                column: "TlsrptReportPolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_TlsrptReportPolicies_TlsrptReportId",
                table: "TlsrptReportPolicies",
                column: "TlsrptReportId");

            migrationBuilder.CreateIndex(
                name: "IX_TlsrptReports_DomainId_ReportingOrg_ReportId",
                table: "TlsrptReports",
                columns: new[] { "DomainId", "ReportingOrg", "ReportId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TlsrptFailureDetails");

            migrationBuilder.DropTable(
                name: "TlsrptReportPolicies");

            migrationBuilder.DropTable(
                name: "TlsrptReports");
        }
    }
}
