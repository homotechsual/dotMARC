using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddGroupBranding : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "GroupBrandings",
                columns: table => new
                {
                    GroupId = table.Column<int>(type: "integer", nullable: false),
                    DisplayName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    LogoImageId = table.Column<Guid>(type: "uuid", nullable: true),
                    DarkLogoImageId = table.Column<Guid>(type: "uuid", nullable: true),
                    PrimaryColour = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true),
                    SecondaryColour = table.Column<string>(type: "character varying(7)", maxLength: 7, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_GroupBrandings", x => x.GroupId);
                    table.ForeignKey(
                        name: "FK_GroupBrandings_Groups_GroupId",
                        column: x => x.GroupId,
                        principalTable: "Groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "GroupBrandings");
        }
    }
}
