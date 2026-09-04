using DotMarc.DnsPush;
using Xunit;

namespace DotMarc.Tests.DnsPush;

public sealed class DnsRecordPushDecisionTests
{
    [Fact]
    public void NeedsConfirmation_ReturnsFalse_WhenNothingExistsYet()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: null, delegatedToCname: null, proposedValue: "new-value");

        Assert.False(result);
    }

    [Fact]
    public void NeedsConfirmation_ReturnsFalse_WhenExistingAlreadyMatchesProposed()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: "same-value", delegatedToCname: null, proposedValue: "same-value");

        Assert.False(result);
    }

    [Fact]
    public void NeedsConfirmation_ReturnsTrue_WhenExistingDiffersFromProposed()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: "old-value", delegatedToCname: null, proposedValue: "new-value");

        Assert.True(result);
    }

    [Fact]
    public void NeedsConfirmation_ReturnsTrue_WhenDelegatedToCname_EvenIfNoDirectValue()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: null, delegatedToCname: "target.example.com.", proposedValue: "new-value");

        Assert.True(result);
    }

    [Fact]
    public void NeedsConfirmation_ReturnsTrue_WhenDelegatedToCname_RegardlessOfValueMatch()
    {
        var result = DnsRecordPushDecision.NeedsConfirmation(existingValue: "new-value", delegatedToCname: "target.example.com.", proposedValue: "new-value");

        Assert.True(result);
    }
}
