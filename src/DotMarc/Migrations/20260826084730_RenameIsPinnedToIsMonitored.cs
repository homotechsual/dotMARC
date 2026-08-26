using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class RenameIsPinnedToIsMonitored : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsPinned",
                table: "Domains",
                newName: "IsMonitored");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "IsMonitored",
                table: "Domains",
                newName: "IsPinned");
        }
    }
}
