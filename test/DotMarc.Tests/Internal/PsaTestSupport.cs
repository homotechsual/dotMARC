using DotMarc.Data;
using DotMarc.Notifications;
using DotMarc.Psa;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace DotMarc.Tests.Internal;

internal static class PsaTestSupport
{
    /// <summary>A ticket service whose only PSA is HaloPSA, talking to the given Halo client.</summary>
    public static PsaTicketService ForHalo(IHaloPsaClient haloClient) =>
        new([new HaloPsaProvider(haloClient)], NullLogger<PsaTicketService>.Instance);

    /// <summary>Switches HaloPSA on with every setting ticketing needs, so the Halo provider reports ready.</summary>
    public static async Task MakeHaloReadyAsync(DotMarcDbContext context)
    {
        var settings = await context.HaloPsaSettings.SingleAsync();
        settings.Enabled = true;
        settings.AccountName = "contoso";
        settings.AuthServerUrl = "https://contoso.halopsa.com/auth";
        settings.ResourceServerUrl = "https://contoso.halopsa.com/api";
        settings.ClientId = "client";
        settings.ClientSecretConfigured = true;
        settings.TicketTypeId = 1;
        settings.DefaultPriorityId = 2;
        settings.ClosedStatusId = 9;
        await context.SaveChangesAsync();
    }

    /// <summary>A HaloPSA link to the given client, for a Group's or Domain's <c>PsaCompanyLinks</c>.</summary>
    public static PsaCompanyLink HaloLink(string clientId) => new() { Psa = PsaKind.HaloPsa, CompanyId = clientId, CompanyName = $"Client {clientId}" };
}
