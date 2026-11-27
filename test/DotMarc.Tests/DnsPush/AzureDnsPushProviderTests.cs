using DotMarc.DnsPush;
using Xunit;
using System.Net;
using Azure.Core.Pipeline;
using Azure.ResourceManager;
using DotMarc.Tests.Internal;

namespace DotMarc.Tests.DnsPush;

public sealed class AzureDnsPushProviderTests
{
    private const string Subscription = "11111111-1111-1111-1111-111111111111";
    private const string OtherSubscription = "22222222-2222-2222-2222-222222222222";
    private const string ZoneId = $"/subscriptions/{Subscription}/resourceGroups/dns/providers/Microsoft.Network/dnsZones/contoso.com";
    private const string Zone = $$$"""{"value":[{"id":"{{{ZoneId}}}","name":"contoso.com","type":"Microsoft.Network/dnsZones","location":"global","properties":{}}]}""";
    private const string Subscriptions = $$$"""{"value":[{"id":"/subscriptions/{{{Subscription}}}","subscriptionId":"{{{Subscription}}}","displayName":"DNS","state":"Enabled"}]}""";
    private static DnsRecordChange Change(DnsRecordChangeKind kind = DnsRecordChangeKind.Create, string type = "TXT", string? existing = null) =>
        new(kind, type, "_dmarc.contoso.com", "v=DMARC1; p=reject", null, "contoso.com", ExistingRecordType: existing);

    private sealed record TestClient(ArmClient Arm, HttpPipeline Pipeline);
    private static Task<DnsPushResult> PushAsync(TestClient client, IReadOnlyList<DnsRecordChange> changes) =>
        AzureDnsPushProvider.PushChangesAsync(client.Arm, client.Pipeline, changes, CancellationToken.None);

    private static (TestClient Client, FakeHttpMessageHandler Handler) Client(params (HttpStatusCode Status, string Body)[] responses)
    {
        var handler = new FakeHttpMessageHandler();
        foreach (var response in responses)
        {
            handler.StatusCodes.Enqueue(response.Status);
            handler.ResponseBodies.Enqueue(response.Body);
        }
        var options = new ArmClientOptions { Transport = new HttpClientTransport(new HttpClient(handler)) };
        options.Retry.MaxRetries = 0;
        var credential = new FakeTokenCredential();
        return (new(new ArmClient(credential, Subscription, options),
            HttpPipelineBuilder.Build(options, new BearerTokenAuthenticationPolicy(credential, "https://management.azure.com/.default"))), handler);
    }

    [Theory]
    [InlineData("*", "", true)]
    [InlineData("Microsoft.Network/dnsZones/*", "", true)]
    [InlineData("microsoft.network/dnszones/txt/write", "", true)]
    [InlineData("*/read", "", false)]
    [InlineData("*", "Microsoft.Network/dnsZones/TXT/write", false)]
    [InlineData("*", "Microsoft.Network/dnsZones/*/delete", true)]
    public void PermissionWildcardsAndNotActionsAreApplied(string allowed, string denied, bool expected) =>
        Assert.Equal(expected, AzureDnsPushProvider.AllowsAction([allowed], string.IsNullOrEmpty(denied) ? [] : [denied], "Microsoft.Network/dnsZones/TXT/write"));

    [Fact]
    public async Task DeniedSubscriptionListingIsExplicitAndNotZoneNotFound()
    {
        var (client, handler) = Client((HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed","message":"denied"}}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Contains("denied access", result.DetailMessage);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task InaccessibleSubscriptionDoesNotPreventFindingAnotherZone()
    {
        var subscriptions = $$$"""{"value":[{"id":"/subscriptions/{{{OtherSubscription}}}","subscriptionId":"{{{OtherSubscription}}}","displayName":"Other","state":"Enabled"},{"id":"/subscriptions/{{{Subscription}}}","subscriptionId":"{{{Subscription}}}","displayName":"DNS","state":"Enabled"}]}""";
        var (client, handler) = Client((HttpStatusCode.OK, subscriptions),
            (HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed","message":"denied"}}"""),
            (HttpStatusCode.OK, Zone), (HttpStatusCode.OK, """{"value":[{"actions":["*"],"notActions":[]}]}"""),
            (HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"not found"}}"""),
            (HttpStatusCode.OK, $$$"""{"id":"{{{ZoneId}}}/TXT/_dmarc","name":"_dmarc","type":"Microsoft.Network/dnsZones/TXT","properties":{"TTL":3600,"TXTRecords":[{"value":["v=DMARC1; p=reject"]}]}}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.Pushed, result.Outcome);
        Assert.Contains(handler.Requests, request => request.Method == HttpMethod.Put);
    }

    [Fact]
    public async Task DeniedZoneListingDoesNotClaimZoneIsAbsent()
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions),
            (HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed","message":"denied"}}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Contains("Could not establish", result.DetailMessage);
        Assert.Contains(Subscription, result.DetailMessage);
        Assert.DoesNotContain(handler.Requests, request => request.Method != HttpMethod.Get);
    }

    [Fact]
    public async Task CompletedEmptyDiscoveryReturnsZoneNotFound()
    {
        var (client, _) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, """{"value":[]}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ZoneNotFound, result.Outcome);
        Assert.Contains("1 visible subscription", result.DetailMessage);
    }

    [Fact]
    public async Task NoVisibleSubscriptionsExplainsAccountAndTenant()
    {
        var (client, _) = Client((HttpStatusCode.OK, """{"value":[]}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ZoneNotFound, result.Outcome);
        Assert.Contains("No subscriptions", result.DetailMessage);
    }

    [Theory]
    [InlineData("""{"value":[{"actions":["*/read"],"notActions":[]}]}""", "TXT/write")]
    [InlineData("""{"value":[{"actions":["*"],"notActions":["*/write"]}]}""", "TXT/write")]
    [InlineData("""{"value":[]}""", "TXT/write")]
    public async Task MissingWritePermissionStopsBeforeAnyRecordRequest(string permissions, string missing)
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, Zone), (HttpStatusCode.OK, permissions));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Contains(missing, result.DetailMessage);
        Assert.Equal(3, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task ReplacementRequiresDeletePermissionBeforeDeleting()
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, Zone),
            (HttpStatusCode.OK, """{"value":[{"actions":["*"],"notActions":["*/delete"]}]}"""));
        var result = await PushAsync(client, [Change(DnsRecordChangeKind.Replace, existing: "CNAME")]);
        Assert.Contains("CNAME/delete", result.DetailMessage);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Theory]
    [InlineData(401, "rejected the sign-in")]
    [InlineData(403, "denied access")]
    [InlineData(429, "rate-limited")]
    [InlineData(500, "HTTP 500")]
    public async Task PermissionLookupErrorsDoNotWrite(int status, string expected)
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, Zone),
            ((HttpStatusCode)status, """{"error":{"code":"Error","message":"failed"}}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Contains(expected, result.DetailMessage);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"value":[{"actions":["*"],"notActions":"bad"}]}""")]
    [InlineData("""{"value":[{"actions":[123]}]}""")]
    [InlineData("""{"value":[null]}""")]
    public async Task MalformedPermissionsCannotBecomeSuccess(string permissions)
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, Zone), (HttpStatusCode.OK, permissions));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task DuplicateZoneNamesAreNotChosenArbitrarily()
    {
        var duplicate = $$$"""{"value":[{"id":"{{{ZoneId}}}","name":"contoso.com","type":"Microsoft.Network/dnsZones","location":"global","properties":{}},{"id":"/subscriptions/{{{Subscription}}}/resourceGroups/other/providers/Microsoft.Network/dnsZones/contoso.com","name":"contoso.com","type":"Microsoft.Network/dnsZones","location":"global","properties":{}}]}""";
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, duplicate));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Contains("More than one", result.DetailMessage);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task PermissionsArePagedAndSeparateRolesCanGrantExcludedActions()
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, Zone),
            (HttpStatusCode.OK, $$$"""{"value":[{"actions":["*"],"notActions":["*/write"]}],"nextLink":"https://management.azure.com{{{ZoneId}}}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01&skip=1"}"""),
            (HttpStatusCode.OK, """{"value":[{"actions":["Microsoft.Network/dnsZones/TXT/write"],"notActions":[]}]}"""),
            (HttpStatusCode.NotFound, """{"error":{"code":"ResourceNotFound","message":"not found"}}"""),
            (HttpStatusCode.OK, $$$"""{"id":"{{{ZoneId}}}/TXT/_dmarc","name":"_dmarc","type":"Microsoft.Network/dnsZones/TXT","properties":{"TTL":3600,"TXTRecords":[{"value":["v=DMARC1; p=reject"]}]}}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.Pushed, result.Outcome);
        var permissionRequests = handler.Requests.Where(request => request.RequestUri!.AbsolutePath.EndsWith("/permissions")).ToList();
        Assert.Equal(2, permissionRequests.Count);
        Assert.All(permissionRequests, request => Assert.Equal("Bearer", request.Headers.Authorization?.Scheme));
    }

    [Theory]
    [InlineData("https://unexpected.example/permissions")]
    [InlineData("http://management.azure.com/permissions")]
    [InlineData("https://management.azure.com:8443/permissions")]
    [InlineData("not a URI")]
    public async Task InvalidPermissionPaginationStopsWithoutFollowingIt(string nextLink)
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, Zone),
            (HttpStatusCode.OK, $$"""{"value":[{"actions":["*"],"notActions":[]}],"nextLink":"{{nextLink}}"}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task EveryChangeIsCheckedBeforeTheFirstWrite()
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, Zone),
            (HttpStatusCode.OK, """{"value":[{"actions":["*"],"notActions":["*/delete"]}]}"""));
        var result = await PushAsync(client, [Change(), Change(DnsRecordChangeKind.Replace, existing: "CNAME")]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Contains("CNAME/delete", result.DetailMessage);
        Assert.All(handler.Requests, request => Assert.Equal(HttpMethod.Get, request.Method));
    }

    [Fact]
    public async Task SubscriptionPaginationStillFindsZoneInLaterSubscription()
    {
        var first = $$$"""{"value":[{"id":"/subscriptions/{{{OtherSubscription}}}","subscriptionId":"{{{OtherSubscription}}}","displayName":"Other","state":"Enabled"}],"nextLink":"https://management.azure.com/subscriptions?api-version=2022-12-01&skip=1"}""";
        var (client, handler) = Client((HttpStatusCode.OK, first), (HttpStatusCode.OK, """{"value":[]}"""),
            (HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, Zone),
            (HttpStatusCode.Forbidden, """{"error":{"code":"AuthorizationFailed","message":"denied"}}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Contains("checking permissions", result.DetailMessage);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Fact]
    public async Task ZonePaginationStillFindsZoneOnLaterPage()
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions),
            (HttpStatusCode.OK, $$$"""{"value":[],"nextLink":"https://management.azure.com/subscriptions/{{{Subscription}}}/providers/Microsoft.Network/dnsZones?api-version=2018-05-01&skip=1"}"""),
            (HttpStatusCode.OK, Zone), (HttpStatusCode.OK, """{"value":[{"actions":["*/read"],"notActions":[]}]}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Contains("TXT/write", result.DetailMessage);
        Assert.Equal(4, handler.Requests.Count);
    }

    [Fact]
    public async Task RepeatingPermissionPageIsRejected()
    {
        var (client, handler) = Client((HttpStatusCode.OK, Subscriptions), (HttpStatusCode.OK, Zone),
            (HttpStatusCode.OK, $$$"""{"value":[{"actions":["*"],"notActions":[]}],"nextLink":"https://management.azure.com{{{ZoneId}}}/providers/Microsoft.Authorization/permissions?api-version=2022-04-01"}"""));
        var result = await PushAsync(client, [Change()]);
        Assert.Equal(DnsPushOutcome.ProviderError, result.Outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task CallerCancellationIsNotReportedAsSuccessOrMissingZone()
    {
        var (client, _) = Client();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AzureDnsPushProvider.PushChangesAsync(client.Arm, client.Pipeline, [Change()], cancellation.Token));
    }

    [Theory]
    [InlineData("contoso.com", "contoso.com", "@")]
    [InlineData("mta-sts.contoso.com", "contoso.com", "mta-sts")]
    [InlineData("selector1._domainkey.mail.contoso.co.uk", "contoso.co.uk", "selector1._domainkey.mail")]
    [InlineData("Contoso.com.", "contoso.com", "@")]
    public void RelativeName_IsTheApexSymbolOrThePartBeforeTheZone(string recordName, string zoneName, string expected)
    {
        Assert.Equal(expected, AzureDnsPushProvider.RelativeName(recordName, zoneName));
    }
}
