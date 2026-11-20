using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddDnsPushBatches : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "DnsPushBatches",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedBy = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Target = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ExpiresUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DnsPushBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DnsPushBatchDomains",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainId = table.Column<int>(type: "integer", nullable: false),
                    DomainName = table.Column<string>(type: "character varying(253)", maxLength: 253, nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    Included = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PlanFingerprint = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    FinishedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DnsPushBatchDomains", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DnsPushBatchDomains_DnsPushBatches_BatchId",
                        column: x => x.BatchId,
                        principalTable: "DnsPushBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DnsPushBatchDomains_BatchId_Position",
                table: "DnsPushBatchDomains",
                columns: new[] { "BatchId", "Position" });

            migrationBuilder.CreateIndex(
                name: "IX_DnsPushBatches_ExpiresUtc",
                table: "DnsPushBatches",
                column: "ExpiresUtc");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DnsPushBatchDomains");

            migrationBuilder.DropTable(
                name: "DnsPushBatches");
        }
    }
}
