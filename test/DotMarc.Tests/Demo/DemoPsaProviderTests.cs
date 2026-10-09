using DotMarc.Demo;
using DotMarc.Psa;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Demo;

public sealed class DemoPsaProviderTests
{
    [Fact]
    public async Task ADemoTicket_ReadsOpen_ThenClosedAfterThreeMinutes()
    {
        var clock = new FixedTimeProvider(new DateTimeOffset(2026, 10, 6, 12, 0, 0, TimeSpan.Zero));
        var provider = new DemoPsaProvider(PsaKind.ConnectWise, clock);

        var ticketId = await provider.CreateTicketAsync(null!, new PsaTicketRequest("1", "contoso.io", "MissedReport", "t", "m"));
        Assert.Equal(PsaTicketState.Open, await provider.GetTicketStateAsync(null!, ticketId));

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(PsaTicketState.Closed, await provider.GetTicketStateAsync(null!, ticketId));
        Assert.Equal(PsaTicketState.Missing, await provider.GetTicketStateAsync(null!, "nope"));
        Assert.Equal(5, (await provider.ListCompaniesAsync(null!)).Count);
        Assert.True((await provider.GetReadinessAsync(null!)).IsReady);
    }

    [Fact]
    public async Task ClosingADemoTicket_ClosesItStraightAway()
    {
        var provider = new DemoPsaProvider(PsaKind.Autotask, new FixedTimeProvider(DateTimeOffset.UnixEpoch));
        var ticketId = await provider.CreateTicketAsync(null!, new PsaTicketRequest("1", "contoso.io", "MissedReport", "t", "m"));

        await provider.CloseTicketAsync(null!, ticketId, "done");

        Assert.Equal(PsaTicketState.Closed, await provider.GetTicketStateAsync(null!, ticketId));
    }
}
