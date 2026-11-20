using DotMarc.Notifications;
using Xunit;

namespace DotMarc.Tests.Notifications;

public sealed class AlertRuleValidatorTests
{
    private static AlertRuleInput Valid(Dictionary<string, string>? parameters = null, IReadOnlyList<AlertRuleScopeInput>? scope = null, string name = "A rule",
        string severity = "Warning", int? cooldown = null, int? autoClose = null) =>
        new(name, Enabled: true, severity, StopProcessing: true, cooldown, autoClose, parameters ?? [], scope ?? []);

    private static IReadOnlyList<string> Problems(string kind, AlertRuleInput input) => AlertRuleValidator.Problems(kind, input);

    [Fact]
    public void AValidInput_HasNoProblems()
    {
        var input = Valid(
            new Dictionary<string, string> { [AlertRuleParameterKeys.MissingDays] = "5" },
            [new AlertRuleScopeInput(AlertRuleScopeTarget.Group, 3, false), new AlertRuleScopeInput(AlertRuleScopeTarget.Domain, 9, true)],
            cooldown: 60);

        Assert.Empty(Problems(AlertTypes.MissedReport, input));
    }

    [Fact]
    public void AMissingParameter_IsAllowed_BecauseItFallsBackToTheDefault()
    {
        Assert.Empty(Problems(AlertTypes.MissedReport, Valid()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ABlankName_IsAProblem(string name)
    {
        Assert.Contains(Problems(AlertTypes.MissedReport, Valid(name: name)), problem => problem.Contains("Name"));
    }

    [Fact]
    public void ANameOverTwoHundredCharacters_IsAProblem()
    {
        Assert.Contains(Problems(AlertTypes.MissedReport, Valid(name: new string('x', 201))), problem => problem.Contains("Name"));
        Assert.Empty(Problems(AlertTypes.MissedReport, Valid(name: new string('x', 200))));
    }

    [Fact]
    public void AnUnknownSeverity_IsAProblem()
    {
        Assert.Contains(Problems(AlertTypes.MissedReport, Valid(severity: "Bogus")), problem => problem.Contains("Severity"));
        Assert.Empty(Problems(AlertTypes.MissedReport, Valid(severity: "Critical")));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(10081)]
    public void ACooldownOutOfRange_IsAProblem(int cooldown)
    {
        Assert.Contains(Problems(AlertTypes.MissedReport, Valid(cooldown: cooldown)), problem => problem.Contains("Cooldown"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(10080)]
    public void ACooldownAtTheEdges_IsFine(int cooldown)
    {
        Assert.Empty(Problems(AlertTypes.MissedReport, Valid(cooldown: cooldown)));
    }

    [Fact]
    public void AutoClose_IsAProblem_OnAKindThatHasNone()
    {
        Assert.Contains(Problems(AlertTypes.MissedReport, Valid(autoClose: 3)), problem => problem.Contains("Close after"));
        Assert.Empty(Problems(AlertTypes.DmarcPolicyWeakened, Valid(autoClose: 3)));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(366)]
    public void AutoCloseOutOfRange_IsAProblem(int days)
    {
        Assert.Contains(Problems(AlertTypes.NameserversChanged, Valid(autoClose: days)), problem => problem.Contains("Close after"));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("91")]
    [InlineData("abc")]
    [InlineData("2.5")]
    [InlineData("")]
    public void AnIntegerParameterOutOfRangeOrNotANumber_IsAProblem_NamingIt(string value)
    {
        var problems = Problems(AlertTypes.MissedReport, Valid(new Dictionary<string, string> { [AlertRuleParameterKeys.MissingDays] = value }));

        Assert.Contains(problems, problem => problem.Contains("Days without a report"));
    }

    [Fact]
    public void AChoiceParameterThatIsNotAChoice_IsAProblem_NamingIt()
    {
        var problems = Problems(AlertTypes.SpfRecordBroken, Valid(new Dictionary<string, string> { [AlertRuleParameterKeys.Mode] = "Off" }));

        Assert.Contains(problems, problem => problem.Contains("Alert when"));
    }

    [Fact]
    public void AParameterTheKindDoesNotDefine_IsAProblem()
    {
        var problems = Problems(AlertTypes.MissedReport, Valid(new Dictionary<string, string> { ["bogus"] = "1" }));

        Assert.Contains(problems, problem => problem.Contains("bogus"));
    }

    [Fact]
    public void AScopeOnASystemKind_IsAProblem()
    {
        var input = Valid(scope: [new AlertRuleScopeInput(AlertRuleScopeTarget.Group, 3, false)]);

        Assert.Contains(Problems(AlertTypes.ApiKeyExpiring, input), problem => problem.Contains("scope"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-4)]
    public void AScopeTargetWithoutAValidId_IsAProblem(int targetId)
    {
        var input = Valid(scope: [new AlertRuleScopeInput(AlertRuleScopeTarget.Tag, targetId, false)]);

        Assert.Contains(Problems(AlertTypes.MissedReport, input), problem => problem.Contains("scope"));
    }

    [Fact]
    public void TheSameScopeEntryTwice_IsAProblem()
    {
        var entry = new AlertRuleScopeInput(AlertRuleScopeTarget.Group, 3, false);

        Assert.Contains(Problems(AlertTypes.MissedReport, Valid(scope: [entry, entry])), problem => problem.Contains("twice"));
    }

    [Fact]
    public void AnUnknownKind_IsAProblem()
    {
        Assert.Contains(Problems("NoSuchKind", Valid()), problem => problem.Contains("NoSuchKind"));
    }

    [Fact]
    public void SeveralProblems_AreAllReported()
    {
        var input = Valid(
            new Dictionary<string, string> { [AlertRuleParameterKeys.MissingDays] = "0" }, name: "", severity: "Bogus", cooldown: -5);

        Assert.True(Problems(AlertTypes.MissedReport, input).Count >= 4);
    }
}
