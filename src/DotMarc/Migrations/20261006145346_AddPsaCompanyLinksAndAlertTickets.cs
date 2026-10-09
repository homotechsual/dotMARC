using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <summary>Adds the shared PSA tables and copies HaloPSA's client and ticket ids into them. Hand-edited: the copy is
    /// added after the generated tables.</summary>
    public partial class AddPsaCompanyLinksAndAlertTickets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AlertTickets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    AlertEventId = table.Column<int>(type: "integer", nullable: false),
                    Psa = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    TicketId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IsOpen = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    LastCheckedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AlertTickets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AlertTickets_AlertEvents_AlertEventId",
                        column: x => x.AlertEventId,
                        principalTable: "AlertEvents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PsaCompanyLinks",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Psa = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: true),
                    DomainId = table.Column<int>(type: "integer", nullable: true),
                    CompanyId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CompanyName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PsaCompanyLinks", x => x.Id);
                    table.CheckConstraint("CK_PsaCompanyLinks_OneOwner", "(\"GroupId\" IS NULL) <> (\"DomainId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_PsaCompanyLinks_Domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "Domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_PsaCompanyLinks_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AlertTickets_AlertEventId_Psa",
                table: "AlertTickets",
                columns: new[] { "AlertEventId", "Psa" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AlertTickets_Psa_TicketId",
                table: "AlertTickets",
                columns: new[] { "Psa", "TicketId" });

            migrationBuilder.CreateIndex(
                name: "IX_PsaCompanyLinks_DomainId",
                table: "PsaCompanyLinks",
                column: "DomainId");

            migrationBuilder.CreateIndex(
                name: "IX_PsaCompanyLinks_GroupId",
                table: "PsaCompanyLinks",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_PsaCompanyLinks_Psa_DomainId",
                table: "PsaCompanyLinks",
                columns: new[] { "Psa", "DomainId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PsaCompanyLinks_Psa_GroupId",
                table: "PsaCompanyLinks",
                columns: new[] { "Psa", "GroupId" },
                unique: true);

            // Halo was the only PSA, and stored its client and ticket ids on the old columns. Copy them in so an upgrade keeps
            // its mappings and open tickets. The client name isn't known here; the id stands in until a page loads Halo's list.
            migrationBuilder.Sql(@"
                INSERT INTO ""PsaCompanyLinks"" (""Psa"", ""GroupId"", ""DomainId"", ""CompanyId"", ""CompanyName"")
                SELECT 'HaloPsa', ""Id"", NULL, ""HaloClientId""::text, ""HaloClientId""::text FROM ""Groups"" WHERE ""HaloClientId"" IS NOT NULL;
                INSERT INTO ""PsaCompanyLinks"" (""Psa"", ""GroupId"", ""DomainId"", ""CompanyId"", ""CompanyName"")
                SELECT 'HaloPsa', NULL, ""Id"", ""HaloClientId""::text, ""HaloClientId""::text FROM ""Domains"" WHERE ""HaloClientId"" IS NOT NULL;
                INSERT INTO ""AlertTickets"" (""AlertEventId"", ""Psa"", ""TicketId"", ""IsOpen"", ""CreatedUtc"")
                SELECT ""Id"", 'HaloPsa', ""ExternalTicketId"", NOT ""IsResolved"", ""CreatedUtc"" FROM ""AlertEvents""
                WHERE ""ExternalTicketProvider"" = 'HaloPSA' AND ""ExternalTicketId"" IS NOT NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AlertTickets");

            migrationBuilder.DropTable(
                name: "PsaCompanyLinks");
        }
    }
}
