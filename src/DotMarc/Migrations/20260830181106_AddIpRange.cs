using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddIpRange : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IpRanges",
                columns: table => new
                {
                    RangeStart = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    RangeEnd = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: false),
                    Organization = table.Column<string>(type: "text", nullable: true),
                    Country = table.Column<string>(type: "text", nullable: true),
                    LookedUpUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IpRanges", x => new { x.RangeStart, x.RangeEnd });
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IpRanges");
        }
    }
}
