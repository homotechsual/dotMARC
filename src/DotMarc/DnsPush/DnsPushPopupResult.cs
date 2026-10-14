using System.Net;
using System.Text.Json;

namespace DotMarc.DnsPush;

/// <summary>The /dns-push/{provider}/callback endpoint runs inside a popup window the pushing page opened (see App.razor's
/// dotMarcOpenDnsPush), not in the tab the user is looking at, so every exit point ends here. Some providers' sign-in
/// pages (Cloudflare's sends Cross-Origin-Opener-Policy: same-origin) cut the popup off from its opener, which leaves
/// window.opener null and stops the popup closing itself. So this page says what happened in words, tells the opener
/// over a BroadcastChannel as well as postMessage, and only then tries to close.</summary>
public static class DnsPushPopupResult
{
    public static IResult Close(string outcome, string? pushId = null) => Results.Content(Html(outcome, pushId), "text/html");

    public static string Html(string outcome, string? pushId)
    {
        // The serializer's default encoder escapes <, >, & and quotes, so nothing in the values can end the script.
        var message = JsonSerializer.Serialize(new { dnsPush = outcome, pushId });
        var (heading, detail) = Describe(outcome);
        return $$"""
            <!DOCTYPE html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <title>dotMARC - DNS push</title>
            <style>body { font-family: system-ui, sans-serif; margin: 2rem; line-height: 1.5; color: #1f2937; } h1 { font-size: 1.25rem; }</style>
            </head>
            <body>
            <h1>{{WebUtility.HtmlEncode(heading)}}</h1>
            <p>{{WebUtility.HtmlEncode(detail)}}</p>
            <p>You can close this window.</p>
            <script>
                const result = {{message}};
                try {
                    const channel = new BroadcastChannel("dotmarc-dns-push");
                    channel.postMessage(result);
                    channel.close();
                } catch (error) {
                    // An old browser without BroadcastChannel still has postMessage below.
                }
                if (window.opener) {
                    window.opener.postMessage(result, window.location.origin);
                }
                window.close();
            </script>
            </body>
            </html>
            """;
    }

    private static (string Heading, string Detail) Describe(string outcome) => outcome switch
    {
        "pushed" => ("Records pushed", "dotMARC updated the DNS records. It can take a few minutes for the change to be seen everywhere."),
        "cancelled" => ("Push cancelled", "The sign-in at your DNS provider was cancelled, so nothing was changed."),
        "invalid" => ("Push not started", "This push expired or couldn't be checked, so nothing was changed. Start it again from dotMARC."),
        "zone-not-found" => ("Couldn't find the domain", "Couldn't find the domain in the DNS account you signed in to, so nothing was changed."),
        "unmergeable" => ("Nothing pushed", "dotMARC couldn't safely work out the change to that record, so it needs a manual look."),
        "nothing-to-push" => ("Nothing to push", "DNS already matches what dotMARC would push."),
        "replace-failed" => ("Push failed partway", "The old record was deleted, but creating its replacement failed, so there's no record at that name. Add it by hand in your DNS provider."),
        "spf-changed" => ("SPF changed", "The SPF record changed since you opened the editor, so nothing was pushed. Reopen the editor to start from the current record."),
        "spf-too-many-lookups" => ("Nothing pushed", "That SPF record would need more than 10 DNS lookups, so it wasn't pushed."),
        "spf-duplicate" => ("Push failed partway", "The new SPF record was added, but the old one couldn't be removed, so the domain has two SPF records. Remove the old one in your DNS provider."),
        "error" => ("DNS push failed", "The DNS push failed. Try again, or add the record by hand."),
        _ => ("The DNS push finished", "Go back to dotMARC to see the result."),
    };
}
