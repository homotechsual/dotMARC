using DotMarc.Email;
using Xunit;

namespace DotMarc.Tests.Email;

public sealed class EmailLimitsTests
{
    private static EmailMessage Message(int recipients, int attachmentBytes) => new(
        Enumerable.Range(1, recipients).Select(number => $"person{number}@example.com").ToList(),
        "Subject", "<p>Hi</p>", "Hi",
        attachmentBytes == 0 ? [] : [new EmailAttachment("report.pdf", "application/pdf", new byte[attachmentBytes])]);

    [Fact]
    public void AMessageWithinTheLimits_Passes() => EmailLimits.Check(Message(25, EmailLimits.MaximumAttachmentBytes));

    [Theory]
    [InlineData(0, 0, "There's no one to send this to.")]
    [InlineData(26, 0, "A report can go to at most 25 recipients.")]
    [InlineData(1, EmailLimits.MaximumAttachmentBytes + 1, "The attachment is over 25 MB, too large to send.")]
    public void AMessageOutsideTheLimits_IsRefused(int recipients, int attachmentBytes, string expected)
    {
        var exception = Assert.Throws<EmailSendException>(() => EmailLimits.Check(Message(recipients, attachmentBytes)));

        Assert.Equal(expected, exception.Message);
    }
}
