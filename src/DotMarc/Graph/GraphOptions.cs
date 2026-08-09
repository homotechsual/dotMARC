using System.ComponentModel.DataAnnotations;

namespace DotMarc.Graph;

public sealed class GraphOptions
{
    public const string SectionName = "Graph";

    [Required]
    public required string ClientId { get; set; }

    [Required]
    public required string TenantId { get; set; }

    [Required]
    public required string ClientSecret { get; set; }

    [Required]
    public required string MailboxAddress { get; set; }

    public int PollIntervalSeconds { get; set; } = 300;
}
