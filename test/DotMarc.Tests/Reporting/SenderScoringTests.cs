using DotMarc.Data;
using DotMarc.Reporting;
using Xunit;

namespace DotMarc.Tests.Reporting;

public sealed class SenderScoringTests
{
    private static readonly IReadOnlyCollection<string> NoSpfIncludes = [];

    private static SenderSourceInput Source(string? owner = null, params AuthDetailSummary[] authDetails) =>
        new("203.0.113.9", owner, authDetails);

    private static AuthDetailSummary Dkim(string domain, DmarcMechanismResult result = DmarcMechanismResult.Pass, string? selector = null) =>
        new(DmarcAuthMechanism.Dkim, domain, result, selector);

    private static ServiceScore ScoreFor(IReadOnlyList<ServiceScore> scores, string serviceName) =>
        scores.Single(score => score.Service.Name == serviceName);

    [Fact]
    public void AuthDomainThatPassed_ScoresPointNine()
    {
        var scores = SenderScoring.Score(Source(null, Dkim("bounce.mcsv.net")), NoSpfIncludes);

        Assert.Equal(0.9, ScoreFor(scores, "Mailchimp").Score, precision: 3);
    }

    [Fact]
    public void AuthDomainThatFailed_ScoresPointEight()
    {
        var scores = SenderScoring.Score(Source(null, Dkim("bounce.mcsv.net", DmarcMechanismResult.Fail)), NoSpfIncludes);

        Assert.Equal(0.8, ScoreFor(scores, "Mailchimp").Score, precision: 3);
    }

    [Fact]
    public void SelectorAlone_ScoresPointSix()
    {
        var scores = SenderScoring.Score(Source(null, Dkim("example.com", selector: "k1")), NoSpfIncludes);

        Assert.Equal(0.6, ScoreFor(scores, "Mailchimp").Score, precision: 3);
    }

    [Fact]
    public void OwnerAlone_ScoresPointFive()
    {
        var scores = SenderScoring.Score(Source("Google LLC"), NoSpfIncludes);

        Assert.Equal(0.5, ScoreFor(scores, "Google Workspace").Score, precision: 3);
    }

    [Fact]
    public void SpfIncludeAlone_ScoresPointTwo_ByEitherOfTheServicesNames()
    {
        var byName = SenderScoring.Score(Source(), ["Zoho"]);
        var byDetectedName = SenderScoring.Score(Source(), ["Zoho Mail"]);

        Assert.Equal(0.2, ScoreFor(byName, "Zoho").Score, precision: 3);
        Assert.Equal(0.2, ScoreFor(byDetectedName, "Zoho").Score, precision: 3);
    }

    [Fact]
    public void TwoSignals_Combine()
    {
        var scores = SenderScoring.Score(Source("Microsoft Corporation", Dkim("example.com", selector: "selector1")), NoSpfIncludes);

        Assert.Equal(0.8, ScoreFor(scores, "Microsoft 365").Score, precision: 3);
    }

    [Fact]
    public void AllFourSignals_ReachPointNineEightFour_NeverOverTheCap()
    {
        var scores = SenderScoring.Score(
            Source("Rocket Science Group", Dkim("mcsv.net", selector: "k1")),
            ["Mailchimp"]);

        var score = ScoreFor(scores, "Mailchimp").Score;

        Assert.Equal(0.984, score, precision: 3);
        Assert.True(score <= SenderScoring.Cap);
        Assert.Equal(0.99, SenderScoring.Cap);
    }

    [Fact]
    public void ASignalTypeIsCountedOncePerService()
    {
        var scores = SenderScoring.Score(Source(null, Dkim("a.mcsv.net"), Dkim("b.mcsv.net")), NoSpfIncludes);

        Assert.Equal(0.9, ScoreFor(scores, "Mailchimp").Score, precision: 3);
    }

    [Fact]
    public void APassingAuthDomain_BeatsAFailingOneForTheSameService()
    {
        var scores = SenderScoring.Score(
            Source(null, Dkim("a.mcsv.net", DmarcMechanismResult.Fail), Dkim("b.mcsv.net")), NoSpfIncludes);

        Assert.Equal(0.9, ScoreFor(scores, "Mailchimp").Score, precision: 3);
    }

    [Fact]
    public void NoSignals_ReturnsEmpty() =>
        Assert.Empty(SenderScoring.Score(Source("Some Hosting Ltd", Dkim("example.com", selector: "default")), NoSpfIncludes));

    [Fact]
    public void NullOwner_DoesNotThrow() =>
        Assert.Empty(SenderScoring.Score(Source(null), NoSpfIncludes));

    [Fact]
    public void TwoServicesAtTheSameScore_AreOrderedByCatalogueOrder()
    {
        var scores = SenderScoring.Score(Source("Microsoft and Google joint venture"), NoSpfIncludes);

        Assert.Equal(["Microsoft 365", "Google Workspace"], scores.Select(score => score.Service.Name));
    }

    [Fact]
    public void ResultsAreHighestScoreFirst()
    {
        var scores = SenderScoring.Score(Source("Google LLC", Dkim("bounce.mcsv.net")), NoSpfIncludes);

        Assert.Equal(["Mailchimp", "Google Workspace"], scores.Select(score => score.Service.Name));
    }

    [Fact]
    public void EvidenceDescriptions_ReadLikeSentences()
    {
        var scores = SenderScoring.Score(
            Source("Rocket Science Group LLC", Dkim("bounce.mcsv.net", selector: "k1")), ["Mailchimp"]);

        var descriptions = ScoreFor(scores, "Mailchimp").Evidence.Select(evidence => evidence.Description).ToList();

        Assert.Contains("DKIM signed by bounce.mcsv.net and passed", descriptions);
        Assert.Contains("DKIM selector k1 is used by Mailchimp", descriptions);
        Assert.Contains("IP owned by Rocket Science Group LLC", descriptions);
        Assert.Contains("The domain's SPF record includes Mailchimp", descriptions);
    }

    [Fact]
    public void AnAlignedMicrosoftSender_SigningAsTheCustomersOwnDomain_IsStillFoundByOwnerAndSelector()
    {
        var scores = SenderScoring.Score(
            Source("Microsoft Corporation", Dkim("contoso.com", selector: "selector2")), NoSpfIncludes);

        Assert.True(ScoreFor(scores, "Microsoft 365").Score >= SenderScoring.StrongFrom);
    }
}
