using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "BrandingImages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Bytes = table.Column<byte[]>(type: "bytea", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UploadedUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandingImages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "BrandingSettings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ProductName = table.Column<string>(type: "character varying(60)", maxLength: 60, nullable: false),
                    PrimaryColour = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    SecondaryColour = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: false),
                    LogoImageId = table.Column<Guid>(type: "uuid", nullable: true),
                    DarkLogoImageId = table.Column<Guid>(type: "uuid", nullable: true),
                    SupportEmail = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    SupportUrl = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    SupportPhone = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                    FooterText = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BrandingSettings", x => x.Id);
                });

            migrationBuilder.InsertData(
                table: "BrandingSettings",
                columns: new[] { "Id", "DarkLogoImageId", "FooterText", "LogoImageId", "PrimaryColour", "ProductName", "SecondaryColour", "SupportEmail", "SupportPhone", "SupportUrl" },
                values: new object[] { 1, null, null, null, "#E3594F", "dotMARC", "#263141", null, null, null });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BrandingImages");

            migrationBuilder.DropTable(
                name: "BrandingSettings");
        }
    }
}
