using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace QUEUEDUP.Pages;

public class SpotifyCallbackModel : PageModel
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;

    public SpotifyCallbackModel(IHttpClientFactory http, IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    public async Task<IActionResult> OnGetAsync(string? code, string? state, string? error)
    {
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return RedirectToPage("/Index");

        var savedState = HttpContext.Session.GetString("SpotifyState");
        if (state != savedState)
            return RedirectToPage("/Index");

        var clientId     = _config["Spotify:ClientId"]     ?? "";
        var clientSecret = _config["Spotify:ClientSecret"] ?? "";
        var redirectUri  = _config["Spotify:RedirectUri"]  ?? "";

        var client      = _http.CreateClient();
        var credentials = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));

        var req = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]   = "authorization_code",
            ["code"]         = code,
            ["redirect_uri"] = redirectUri,
        });

        try
        {
            var resp  = await client.SendAsync(req);
            var doc   = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());

            if (doc.RootElement.TryGetProperty("access_token", out var tokenProp))
            {
                var accessToken = tokenProp.GetString() ?? "";
                HttpContext.Session.SetString("SpotifyToken", accessToken);

                var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
                profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
                var profileResp = await client.SendAsync(profileReq);
                var profileDoc  = JsonDocument.Parse(await profileResp.Content.ReadAsStringAsync());

                if (profileDoc.RootElement.TryGetProperty("display_name", out var dn))
                    HttpContext.Session.SetString("SpotifyUser", dn.GetString() ?? "");
            }
        }
        catch { }

        return RedirectToPage("/Index");
    }
}
