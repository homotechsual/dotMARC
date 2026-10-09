using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DotMarc.Migrations
{
    /// <summary>Drops HaloPSA's old client and ticket columns, now that the shared PSA tables hold them. Hand-edited: Down
    /// copies Halo's data back.</summary>
    public partial class RemoveHaloColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "HaloClientId",
                table: "Groups");

            migrationBuilder.DropColumn(
                name: "HaloClientId",
                table: "Domains");

            migrationBuilder.DropColumn(
                name: "ExternalTicketId",
                table: "AlertEvents");

            migrationBuilder.DropColumn(
                name: "ExternalTicketProvider",
                table: "AlertEvents");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "HaloClientId",
                table: "Groups",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "HaloClientId",
                table: "Domains",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTicketId",
                table: "AlertEvents",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ExternalTicketProvider",
                table: "AlertEvents",
                type: "text",
                nullable: true);

            // ConnectWise and Autotask data has nowhere to go in the old shape, so it stays only in the shared tables
            // (which the previous migration's Down drops).
            migrationBuilder.Sql(@"
                UPDATE ""Groups"" SET ""HaloClientId"" = link.""CompanyId""::int FROM ""PsaCompanyLinks"" link
                WHERE link.""GroupId"" = ""Groups"".""Id"" AND link.""Psa"" = 'HaloPsa';
                UPDATE ""Domains"" SET ""HaloClientId"" = link.""CompanyId""::int FROM ""PsaCompanyLinks"" link
                WHERE link.""DomainId"" = ""Domains"".""Id"" AND link.""Psa"" = 'HaloPsa';
                UPDATE ""AlertEvents"" SET ""ExternalTicketProvider"" = 'HaloPSA', ""ExternalTicketId"" = ticket.""TicketId"" FROM ""AlertTickets"" ticket
                WHERE ticket.""AlertEventId"" = ""AlertEvents"".""Id"" AND ticket.""Psa"" = 'HaloPsa';");
        }
    }
}
