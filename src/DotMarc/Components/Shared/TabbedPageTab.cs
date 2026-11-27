namespace DotMarc.Components.Shared;

/// <summary>One tab of a <see cref="TabbedPage"/>. Each tab is its own routable page, so <paramref name="Href"/> is that page's address.</summary>
public sealed record TabbedPageTab(string Title, string Href, bool Visible);
