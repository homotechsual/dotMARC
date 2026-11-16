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
}
