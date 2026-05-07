using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QUEUEDUP.Data;

namespace QUEUEDUP.Pages;

public class SpotifyCallbackModel : PageModel
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration     _config;
    private readonly AppDbContext        _db;

    public SpotifyCallbackModel(IHttpClientFactory http, IConfiguration config, AppDbContext db)
    {
        _http   = http;
        _config = config;
        _db     = db;
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
        var credentials  = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));

        var client = _http.CreateClient();

        var tokenReq = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        tokenReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        tokenReq.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]   = "authorization_code",
            ["code"]         = code,
            ["redirect_uri"] = redirectUri,
        });

        try
        {
            var tokenResp = await client.SendAsync(tokenReq);
            var tokenDoc  = JsonDocument.Parse(await tokenResp.Content.ReadAsStringAsync());

            if (!tokenDoc.RootElement.TryGetProperty("access_token", out var accessProp)) return RedirectToPage("/Index");

            var accessToken  = accessProp.GetString() ?? "";
            var refreshToken = tokenDoc.RootElement.TryGetProperty("refresh_token", out var rp) ? rp.GetString() ?? "" : "";
            var expiresIn    = tokenDoc.RootElement.TryGetProperty("expires_in",    out var ep) ? ep.GetInt32() : 3600;
            var expiry       = DateTime.UtcNow.AddSeconds(expiresIn);

            // Fetch user profile for ID + display name
            var profileReq = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me");
            profileReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var profileDoc = JsonDocument.Parse(await (await client.SendAsync(profileReq)).Content.ReadAsStringAsync());

            var spotifyId   = profileDoc.RootElement.TryGetProperty("id",           out var id) ? id.GetString() ?? "" : "";
            var displayName = profileDoc.RootElement.TryGetProperty("display_name", out var dn) ? dn.GetString() ?? spotifyId : spotifyId;

            if (string.IsNullOrEmpty(spotifyId)) return RedirectToPage("/Index");

            // Save or update user in DB
            var user = await _db.SpotifyUsers.FindAsync(spotifyId);
            if (user == null)
            {
                user = new SpotifyUser { Id = spotifyId };
                _db.SpotifyUsers.Add(user);
            }
            user.DisplayName  = displayName;
            user.AccessToken  = accessToken;
            user.TokenExpiry  = expiry;
            if (!string.IsNullOrEmpty(refreshToken)) user.RefreshToken = refreshToken;
            await _db.SaveChangesAsync();

            HttpContext.Session.SetString("SpotifyUserId", spotifyId);
        }
        catch { }

        return RedirectToPage("/Index");
    }
}
