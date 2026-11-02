using DotMarc.Data;
using Microsoft.EntityFrameworkCore;

namespace DotMarc.Portal;

/// <summary>Shares the saved sponsor-link visibility within a Blazor circuit, including immediately after a save.</summary>
public sealed class SponsorLinkState(IDbContextFactory<DotMarcDbContext> dbFactory)
{
    public bool IsVisible { get; private set; }
    public event Action? Changed;

    public async Task RefreshAsync()
    {
        await using var context = await dbFactory.CreateDbContextAsync();
        IsVisible = await context.BrandingSettings.Select(settings => settings.ShowSponsorLink).SingleAsync();
        Changed?.Invoke();
    }
}
