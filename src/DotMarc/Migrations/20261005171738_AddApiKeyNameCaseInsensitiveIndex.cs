using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <inheritdoc />
    public partial class AddApiKeyNameCaseInsensitiveIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ApiKeys_Name",
                table: "ApiKeys");

            // The service already compares names ignoring case; this makes two keys created at the same moment as
            // "Halo" and "halo" collide in the database too.
            migrationBuilder.Sql(@"CREATE UNIQUE INDEX ""IX_ApiKeys_LowerName"" ON ""ApiKeys"" (lower(""Name"")) WHERE ""RevokedUtc"" IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"DROP INDEX ""IX_ApiKeys_LowerName"";");

            migrationBuilder.CreateIndex(
                name: "IX_ApiKeys_Name",
                table: "ApiKeys",
                column: "Name",
                unique: true,
                filter: "\"RevokedUtc\" IS NULL");
        }
    }
}
