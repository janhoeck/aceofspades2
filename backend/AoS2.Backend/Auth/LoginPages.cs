using System.Text.Encodings.Web;
using System.Text.Unicode;

namespace AoS2.Backend.Auth;

/// <summary>
/// Die wenigen HTML-Seiten, die der Browser während der Anmeldung vom Backend sieht.
/// Farben wie im Launcher („Voxel-Schlachtfeld“).
/// </summary>
internal static class LoginPages
{
    public static IResult Error(int statusCode, string title, string text) =>
        Page(statusCode, title, $"""
            <h1>{Encode(title)}</h1>
            <p>{Encode(text)}</p>
            """);

    /// <summary>Ersatz für die Discord-Seite beim Entwicklungs-Login.</summary>
    public static IResult DevAuthorize(string state) =>
        Page(200, "Entwicklungs-Login", $"""
            <h1>Entwicklungs-Login</h1>
            <p>Statt Discord: Mit welchem Namen willst du dich anmelden?</p>
            <form method="get" action="{LoginEndpoints.DevApprovePath}">
              <input type="hidden" name="state" value="{Encode(state)}">
              <input name="name" value="Testspieler" maxlength="32" required autofocus>
              <div class="row">
                <button type="submit">Anmelden</button>
                <button type="submit" name="deny" value="1" class="stone">Abbrechen</button>
              </div>
            </form>
            <p class="hint">Diese Seite gibt es nur, wenn das Backend mit <code>Discord:UseDevLogin</code> läuft.</p>
            """);

    // Umlaute bleiben lesbar (die Seite ist UTF-8), nur HTML-Sonderzeichen werden ersetzt.
    private static readonly HtmlEncoder Html = HtmlEncoder.Create(UnicodeRanges.All);

    private static string Encode(string text) => Html.Encode(text);

    private static IResult Page(int statusCode, string title, string body) => Results.Content($$"""
        <!doctype html>
        <html lang="de">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{Encode(title)}} – Ace of Spades 2</title>
        <style>
          body { margin: 0; min-height: 100vh; display: grid; place-items: center; background: #14181F; color: #EDE6D3;
                 font: 17px/1.5 "Barlow", "Segoe UI", system-ui, sans-serif; }
          main { max-width: 480px; margin: 24px; padding: 28px 32px; background: #0B0E13; border: 2px solid #2A3240; border-bottom-width: 6px; }
          h1 { margin: 0 0 12px; color: #F2C14E; font-size: 26px; }
          p { margin: 0 0 12px; color: #C9C2AF; }
          .hint { color: #8B93A3; font-size: 14px; }
          input { box-sizing: border-box; width: 100%; height: 48px; padding: 0 14px; margin: 8px 0 16px; background: #14181F;
                  color: #EDE6D3; border: 2px solid #2A3240; font: inherit; }
          .row { display: flex; gap: 12px; margin-bottom: 16px; }
          button { height: 48px; padding: 0 24px; border: 0; border-bottom: 4px solid #A9801F; background: #F2C14E; color: #2B1E06;
                   font: inherit; font-weight: 600; cursor: pointer; }
          button.stone { background: #2A3240; color: #EDE6D3; border-bottom-color: #1A2029; }
          button:active { border-bottom-width: 0; border-top: 4px solid transparent; }
        </style>
        </head>
        <body><main>
        {{body}}
        </main></body>
        </html>
        """, "text/html; charset=utf-8", statusCode: statusCode);
}
