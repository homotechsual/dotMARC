using DotMarc.Dns;
using DotMarc.Tests.Internal;
using Xunit;

namespace DotMarc.Tests.Dns;

public sealed class MailServiceDetectorTests
{
    private static (MailServiceDetector detector, FakeHttpMessageHandler handler) CreateDetector()
    {
        var handler = new FakeHttpMessageHandler();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://cloudflare-dns.com/") };
        return (new MailServiceDetector(http), handler);
    }

    [Theory]
    [InlineData("contoso-com.mail.protection.outlook.com", "Microsoft 365")]
    [InlineData("aspmx.l.google.com", "Google Workspace")]
    [InlineData("alt1.aspmx.l.google.com", "Google Workspace")]
    [InlineData("mx.zoho.com", "Zoho Mail")]
    [InlineData("mx2.zohomail.com", "Zoho Mail")]
    [InlineData("mx.zohomail.eu", "Zoho Mail")]
    [InlineData("mx.zohomail.in", "Zoho Mail")]
    [InlineData("in1-smtp.messagingengine.com", "Fastmail")]
    [InlineData("mail.protonmail.ch", "ProtonMail")]
    public async Task DetectAsync_DetectsInboxProvider_FromMxExchange(string mxExchange, string expectedProvider)
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue($$"""{"Status":0,"Answer":[{"type":15,"data":"10 {{mxExchange}}."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":3}""");

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        var service = Assert.Single(result);
        Assert.Equal(expectedProvider, service.ProviderName);
        Assert.Equal(DetectedMailServiceKind.Inbox, service.Kind);
    }

    [Theory]
    [InlineData("_spf.google.com", "Google Workspace")]
    [InlineData("spf.protection.outlook.com", "Microsoft 365")]
    [InlineData("zoho.com", "Zoho")]
    [InlineData("zoho.eu", "Zoho")]
    [InlineData("servers.mcsv.net", "Mailchimp")]
    [InlineData("sendgrid.net", "SendGrid")]
    [InlineData("amazonses.com", "Amazon SES")]
    [InlineData("_spf.salesforce.com", "Salesforce")]
    public async Task DetectAsync_DetectsSendingService_FromSpfInclude(string includeHost, string expectedProvider)
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":3}""");
        handler.ResponseBodies.Enqueue($$"""{"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:{{includeHost}} ~all\""}]}""");

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        var service = Assert.Single(result);
        Assert.Equal(expectedProvider, service.ProviderName);
        Assert.Equal(DetectedMailServiceKind.Sending, service.Kind);
    }

    [Fact]
    public async Task DetectAsync_ReturnsBothInboxAndSendingMatches_WhenBothArePresent()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":15,"data":"1 aspmx.l.google.com."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:servers.mcsv.net ~all\""}]}""");

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Equal(2, result.Count);
        Assert.Contains(result, s => s.ProviderName == "Google Workspace" && s.Kind == DetectedMailServiceKind.Inbox);
        Assert.Contains(result, s => s.ProviderName == "Mailchimp" && s.Kind == DetectedMailServiceKind.Sending);
    }

    [Fact]
    public async Task DetectAsync_ReturnsEmptyList_WhenNothingMatches()
    {
        var (detector, handler) = CreateDetector();
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":15,"data":"10 mail.contoso.io."}]}""");
        handler.ResponseBodies.Enqueue("""{"Status":0,"Answer":[{"type":16,"data":"\"v=spf1 include:_spf.unknown-example.test ~all\""}]}""");

        var result = await detector.DetectAsync("contoso.io", CancellationToken.None);

        Assert.Empty(result);
    }
}
