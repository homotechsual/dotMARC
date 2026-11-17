using DotMarc.DnsPush;
using MudBlazor;

namespace DotMarc.Components.Dialogs;

public static class DnsPushReviewDialogExtensions
{
    /// <summary>Shows what <paramref name="plan"/> would add and remove, and returns true when the person chooses to
    /// continue to their DNS provider. Always false when there is nothing to change.</summary>
    public static async Task<bool> ConfirmDnsPushAsync(this IDialogService dialogService, DnsChangePlan plan)
    {
        var review = DnsPushReview.From(plan);
        var dialog = await dialogService.ShowAsync<DnsPushReviewDialog>("Review DNS changes",
            new DialogParameters<DnsPushReviewDialog> { { x => x.Review, review } },
            new DialogOptions { MaxWidth = MaxWidth.Medium, FullWidth = true });
        var result = await dialog.Result;
        return result is { Canceled: false } && !review.NothingToChange;
    }

    /// <summary>For when dotMARC can't push (no DNS provider connected for the zone): lists the records the person has to add
    /// themselves, in a table with copy buttons. <paramref name="reason"/> says why, as a sentence ending in a full stop.</summary>
    public static async Task ShowManualDnsRecordsAsync(this IDialogService dialogService, DnsChangePlan plan, string reason)
    {
        var dialog = await dialogService.ShowAsync<ManualDnsRecordsDialog>("Add these DNS records",
            new DialogParameters<ManualDnsRecordsDialog> { { x => x.Review, DnsPushReview.From(plan) }, { x => x.Reason, reason } },
            new DialogOptions { MaxWidth = MaxWidth.Large, FullWidth = true });
        await dialog.Result;
    }
}
