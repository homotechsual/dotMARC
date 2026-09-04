namespace DotMarc.DnsPush;

/// <summary>The /dns-push/{provider}/callback endpoint runs inside a popup window the pushing page
/// opened (see App.razor's dotMarcOpenDnsPush), not in the tab the user is actually looking at — so
/// its every exit point closes itself and hands the outcome back via postMessage instead of
/// redirecting, which would just navigate the popup rather than the opener.</summary>
public static class DnsPushPopupResult
{
    public static IResult Close(string outcome) =>
        Results.Content($$"""
            <!DOCTYPE html>
            <html>
            <body>
            <script>
                if (window.opener) {
                    window.opener.postMessage({ dnsPush: "{{outcome}}" }, window.location.origin);
                }
                window.close();
            </script>
            </body>
            </html>
            """, "text/html");
}
