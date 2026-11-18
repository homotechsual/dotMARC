using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddSenderDecisions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SenderDecisions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Scope = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    GroupId = table.Column<int>(type: "integer", nullable: true),
                    DomainId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Key = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DecidedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecidedByName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    DecidedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SenderDecisions", x => x.Id);
                    table.CheckConstraint("CK_SenderDecisions_IpOnlyOnDomains", "\"Kind\" <> 'Ip' OR \"Scope\" = 'Domain'");
                    table.CheckConstraint("CK_SenderDecisions_ScopeOwner", "(\"Scope\" = 'Baseline' AND \"GroupId\" IS NULL AND \"DomainId\" IS NULL) OR (\"Scope\" = 'Group' AND \"GroupId\" IS NOT NULL AND \"DomainId\" IS NULL) OR (\"Scope\" = 'Domain' AND \"DomainId\" IS NOT NULL AND \"GroupId\" IS NULL)");
                    table.ForeignKey(
                        name: "FK_SenderDecisions_Domains_DomainId",
                        column: x => x.DomainId,
                        principalTable: "Domains",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SenderDecisions_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SenderDecisions_DomainId",
                table: "SenderDecisions",
                column: "DomainId");

            migrationBuilder.CreateIndex(
                name: "IX_SenderDecisions_GroupId",
                table: "SenderDecisions",
                column: "GroupId");

            migrationBuilder.CreateIndex(
                name: "IX_SenderDecisions_Scope_GroupId_DomainId_Kind_Key",
                table: "SenderDecisions",
                columns: new[] { "Scope", "GroupId", "DomainId", "Kind", "Key" },
                unique: true)
                .Annotation("Npgsql:NullsDistinct", false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SenderDecisions");
        }
    }
}
