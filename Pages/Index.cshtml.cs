using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using QUEUEDUP.Data;

namespace QUEUEDUP.Pages;

public class EventViewModel
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Venue { get; set; } = "";
    public string City { get; set; } = "";
    public string State { get; set; } = "";
    public string Country { get; set; } = "";
    public string Address { get; set; } = "";
    public string Date { get; set; } = "";
    public string ImageUrl { get; set; } = "";
    public string TicketUrl { get; set; } = "";
    public string StatusCode { get; set; } = "";
    public string PriceRange { get; set; } = "";
}

public class IndexModel : PageModel
{
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration     _config;
    private readonly AppDbContext        _db;

    public List<EventViewModel> Events     { get; set; } = new();
    public List<string>         TopArtists { get; set; } = new();
    public string               SpotifyUser    { get; set; } = "";
    public bool                 SpotifyConnected => !string.IsNullOrEmpty(SpotifyUser);

    public IndexModel(IHttpClientFactory http, IConfiguration config, AppDbContext db)
    {
        _http   = http;
        _config = config;
        _db     = db;
    }

    // Returns valid access token (refreshing if needed), and populates SpotifyUser.
    private async Task<string?> GetValidTokenAsync()
    {
        var userId = HttpContext.Session.GetString("SpotifyUserId");
        if (string.IsNullOrEmpty(userId)) return null;

        var user = await _db.SpotifyUsers.FindAsync(userId);
        if (user == null) { HttpContext.Session.Clear(); return null; }

        SpotifyUser = user.DisplayName;

        if (DateTime.UtcNow >= user.TokenExpiry.AddMinutes(-5))
        {
            if (!await RefreshSpotifyTokenAsync(user))
            { HttpContext.Session.Clear(); SpotifyUser = ""; return null; }
        }

        return user.AccessToken;
    }

    private async Task<bool> RefreshSpotifyTokenAsync(SpotifyUser user)
    {
        var clientId     = _config["Spotify:ClientId"]     ?? "";
        var clientSecret = _config["Spotify:ClientSecret"] ?? "";
        var credentials  = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{clientId}:{clientSecret}"));

        var client = _http.CreateClient();
        var req    = new HttpRequestMessage(HttpMethod.Post, "https://accounts.spotify.com/api/token");
        req.Headers.Authorization = new AuthenticationHeaderValue("Basic", credentials);
        req.Content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"]    = "refresh_token",
            ["refresh_token"] = user.RefreshToken,
        });

        try
        {
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return false;
            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            if (!doc.RootElement.TryGetProperty("access_token", out var t)) return false;

            user.AccessToken = t.GetString() ?? "";
            user.TokenExpiry = DateTime.UtcNow.AddSeconds(
                doc.RootElement.TryGetProperty("expires_in", out var exp) ? exp.GetInt32() : 3600);
            if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && !string.IsNullOrEmpty(rt.GetString()))
                user.RefreshToken = rt.GetString()!;

            await _db.SaveChangesAsync();
            return true;
        }
        catch { return false; }
    }

    public async Task OnGetAsync()
    {
        Events = await FetchEventsAsync(null, null);
        var token = await GetValidTokenAsync();
        if (!string.IsNullOrEmpty(token))
            TopArtists = await FetchSpotifyTopArtistsAsync(token);
    }

    public async Task<IActionResult> OnGetSearchAsync(string? keyword, string? city)
        => Partial("_EventsGrid", await FetchEventsAsync(keyword, city));

    public async Task<IActionResult> OnGetSuggestAsync(string? keyword)
    {
        if (string.IsNullOrWhiteSpace(keyword) || keyword.Length < 2)
            return Content("", "text/html");

        var apiKey = _config["Ticketmaster:ApiKey"];
        var client = _http.CreateClient();
        var names  = new List<string>();

        try
        {
            var url = $"https://app.ticketmaster.com/discovery/v2/suggest?keyword={Uri.EscapeDataString(keyword)}&apikey={apiKey}";
            var doc = JsonDocument.Parse(await client.GetStringAsync(url));
            if (doc.RootElement.TryGetProperty("_embedded", out var emb) &&
                emb.TryGetProperty("attractions", out var attractions))
                foreach (var a in attractions.EnumerateArray().Take(6))
                    if (a.TryGetProperty("name", out var n) && !string.IsNullOrEmpty(n.GetString()))
                        names.Add(n.GetString()!);
        }
        catch { }

        if (names.Count == 0) return Content("", "text/html");

        var sb = new StringBuilder("<ul class=\"suggest-list\" role=\"listbox\">");
        foreach (var name in names)
            sb.Append($"<li class=\"suggest-item\" role=\"option\" data-value=\"{WebUtility.HtmlEncode(name)}\">{WebUtility.HtmlEncode(name)}</li>");
        sb.Append("</ul>");
        return Content(sb.ToString(), "text/html");
    }

    public IActionResult OnGetLoginWithSpotify()
    {
        var clientId    = _config["Spotify:ClientId"];
        var redirectUri = Uri.EscapeDataString(_config["Spotify:RedirectUri"] ?? "");
        var scopes      = Uri.EscapeDataString("user-top-read user-read-private user-read-recently-played playlist-modify-private");
        var state       = Guid.NewGuid().ToString("N")[..16];
        HttpContext.Session.SetString("SpotifyState", state);

        return Redirect(
            $"https://accounts.spotify.com/authorize" +
            $"?client_id={clientId}&response_type=code" +
            $"&redirect_uri={redirectUri}&scope={scopes}&state={state}");
    }

    public IActionResult OnGetLogoutSpotify()
    {
        HttpContext.Session.Clear();
        return RedirectToPage();
    }

    public async Task<IActionResult> OnGetTopStatsAsync(string? timeRange, string? tab)
    {
        var token = await GetValidTokenAsync();
        if (string.IsNullOrEmpty(token)) return Content("", "text/html");

        var userId       = HttpContext.Session.GetString("SpotifyUserId")!;
        var today        = DateTime.UtcNow.Date;
        var spotifyRange = timeRange switch
        {
            "4weeks"   => "short_term",
            "12months" => "long_term",
            _          => "medium_term"
        };
        tab ??= "tracks";

        var client = _http.CreateClient();
        async Task<JsonDocument?> Fetch(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }

        var sb = new StringBuilder();

        if (tab == "tracks")
        {
            var doc    = await Fetch($"https://api.spotify.com/v1/me/top/tracks?limit=10&time_range={spotifyRange}");
            var tracks = new List<(string Name, string Artist, string Uri, string TrackId)>();
            if (doc?.RootElement.TryGetProperty("items", out var items) == true)
                foreach (var t in items.EnumerateArray().Take(10))
                {
                    var name    = t.TryGetProperty("name",    out var tn) ? tn.GetString() ?? "" : "";
                    var artist  = t.TryGetProperty("artists", out var ar) && ar.GetArrayLength() > 0 &&
                                  ar[0].TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                    var uri     = t.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
                    var trackId = t.TryGetProperty("id",  out var id) ? id.GetString() ?? "" : "";
                    tracks.Add((name, artist, uri, trackId));
                }

            // DB rank history
            var prevSnap = await _db.RankSnapshots
                .Where(s => s.SpotifyUserId == userId && s.Type == "tracks"
                         && s.TimeRange == spotifyRange && s.Date < today)
                .OrderByDescending(s => s.Date).FirstOrDefaultAsync();
            var prevList = prevSnap != null
                ? JsonSerializer.Deserialize<List<string>>(prevSnap.ItemsJson) ?? new()
                : new List<string>();

            var todaySnap = await _db.RankSnapshots.FirstOrDefaultAsync(
                s => s.SpotifyUserId == userId && s.Type == "tracks"
                  && s.TimeRange == spotifyRange && s.Date == today);
            var newJson = JsonSerializer.Serialize(tracks.Select(t => t.Name).ToList());
            if (todaySnap == null)
                _db.RankSnapshots.Add(new RankSnapshot { SpotifyUserId = userId, Type = "tracks", TimeRange = spotifyRange, Date = today, ItemsJson = newJson });
            else
                todaySnap.ItemsJson = newJson;
            await _db.SaveChangesAsync();

            HttpContext.Session.SetString($"SpotifyTrackUris_{timeRange}", JsonSerializer.Serialize(tracks.Select(t => t.Uri).ToList()));

            sb.Append("<div class=\"stat-list\">");
            for (int i = 0; i < tracks.Count; i++)
            {
                var (name, artist, _, trackId) = tracks[i];
                var change = GetRankChange(prevList, name, i);
                sb.Append("<div class=\"stat-row\">");
                if (!string.IsNullOrEmpty(trackId))
                    sb.Append($"<button class=\"preview-btn\" data-trackid=\"{WebUtility.HtmlEncode(trackId)}\" onclick=\"qdPreview(this)\" title=\"PREVIEW\">&#9654;</button>");
                sb.Append($"<span class=\"stat-rank\">{i + 1:00}</span>");
                sb.Append("<div class=\"stat-info\">");
                sb.Append($"<span class=\"stat-name\">{WebUtility.HtmlEncode(name)}</span>");
                sb.Append($"<span class=\"stat-sub\">{WebUtility.HtmlEncode(artist)}</span>");
                sb.Append("</div>");
                sb.Append(RenderChange(change));
                sb.Append("</div>");
            }
            sb.Append("</div>");
            if (tracks.Count > 0)
            {
                sb.Append($"<button class=\"create-playlist-btn\" hx-get=\"/Index?handler=CreatePlaylist&timeRange={timeRange}\" hx-target=\"#playlist-result\" hx-swap=\"innerHTML\" hx-indicator=\"#stats-indicator\">+ CREATE PLAYLIST FROM THESE TRACKS</button>");
                sb.Append("<div id=\"playlist-result\"></div>");
            }
        }
        else if (tab == "artists")
        {
            var doc     = await Fetch($"https://api.spotify.com/v1/me/top/artists?limit=10&time_range={spotifyRange}");
            var artists = new List<(string Name, string Genres)>();
            if (doc?.RootElement.TryGetProperty("items", out var items) == true)
                foreach (var a in items.EnumerateArray().Take(10))
                {
                    var name      = a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var genreList = new List<string>();
                    if (a.TryGetProperty("genres", out var g))
                        foreach (var genre in g.EnumerateArray().Take(2))
                            genreList.Add(genre.GetString() ?? "");
                    artists.Add((name, string.Join(" · ", genreList)));
                }

            // DB rank history
            var prevSnap = await _db.RankSnapshots
                .Where(s => s.SpotifyUserId == userId && s.Type == "artists"
                         && s.TimeRange == spotifyRange && s.Date < today)
                .OrderByDescending(s => s.Date).FirstOrDefaultAsync();
            var prevList = prevSnap != null
                ? JsonSerializer.Deserialize<List<string>>(prevSnap.ItemsJson) ?? new()
                : new List<string>();

            var todaySnap = await _db.RankSnapshots.FirstOrDefaultAsync(
                s => s.SpotifyUserId == userId && s.Type == "artists"
                  && s.TimeRange == spotifyRange && s.Date == today);
            var newJson = JsonSerializer.Serialize(artists.Select(a => a.Name).ToList());
            if (todaySnap == null)
                _db.RankSnapshots.Add(new RankSnapshot { SpotifyUserId = userId, Type = "artists", TimeRange = spotifyRange, Date = today, ItemsJson = newJson });
            else
                todaySnap.ItemsJson = newJson;
            await _db.SaveChangesAsync();

            sb.Append("<div class=\"stat-list\">");
            for (int i = 0; i < artists.Count; i++)
            {
                var (name, genres) = artists[i];
                var change = GetRankChange(prevList, name, i);
                sb.Append("<div class=\"stat-row\">");
                sb.Append($"<span class=\"stat-rank\">{i + 1:00}</span>");
                sb.Append("<div class=\"stat-info\">");
                sb.Append($"<span class=\"stat-name\">{WebUtility.HtmlEncode(name)}</span>");
                if (!string.IsNullOrEmpty(genres))
                    sb.Append($"<span class=\"stat-sub\">{WebUtility.HtmlEncode(genres)}</span>");
                sb.Append("</div>");
                sb.Append(RenderChange(change));
                sb.Append($"<button class=\"find-concerts-btn\" data-artist=\"{WebUtility.HtmlEncode(name)}\" onclick=\"qdFindConcerts(this)\">CONCERTS ↓</button>");
                sb.Append("</div>");
            }
            sb.Append("</div>");
        }
        else if (tab == "genres")
        {
            var rawGenres  = new List<string>();
            var artistIds  = new List<string>();
            int artistCount = 0;

            async Task CollectArtistIds(string range)
            {
                var d = await Fetch($"https://api.spotify.com/v1/me/top/artists?limit=50&time_range={range}");
                if (d?.RootElement.TryGetProperty("items", out var items) != true) return;
                foreach (var a in items.EnumerateArray())
                {
                    artistCount++;
                    if (a.TryGetProperty("genres", out var gs))
                        foreach (var g in gs.EnumerateArray())
                        { var gn = g.GetString(); if (!string.IsNullOrEmpty(gn)) rawGenres.Add(gn); }
                    if (a.TryGetProperty("id", out var idEl))
                    { var id = idEl.GetString(); if (!string.IsNullOrEmpty(id) && !artistIds.Contains(id)) artistIds.Add(id); }
                }
            }

            await CollectArtistIds(spotifyRange);
            if (artistIds.Count < 10) await CollectArtistIds("medium_term");
            if (artistIds.Count < 10) await CollectArtistIds("long_term");

            if (rawGenres.Count == 0 && artistIds.Count > 0)
            {
                foreach (var batch in artistIds.Distinct().Chunk(50))
                {
                    var d2 = await Fetch($"https://api.spotify.com/v1/artists?ids={string.Join(",", batch)}");
                    if (d2?.RootElement.TryGetProperty("artists", out var arts) != true) continue;
                    foreach (var a in arts.EnumerateArray())
                        if (a.TryGetProperty("genres", out var gs))
                            foreach (var g in gs.EnumerateArray())
                            { var gn = g.GetString(); if (!string.IsNullOrEmpty(gn)) rawGenres.Add(gn); }
                }
            }

            var genreCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var raw in rawGenres)
            {
                var mapped = MapGenre(raw);
                if (mapped != null)
                    genreCounts[mapped] = genreCounts.GetValueOrDefault(mapped) + 1;
            }

            var topGenres = genreCounts.OrderByDescending(kv => kv.Value).ToList();

            if (topGenres.Count == 0 && rawGenres.Count > 0)
            {
                var rawCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                foreach (var r in rawGenres)
                    rawCounts[r] = rawCounts.GetValueOrDefault(r) + 1;
                topGenres = rawCounts.OrderByDescending(kv => kv.Value).ToList();
            }

            if (topGenres.Count == 0)
            {
                var msg = artistCount == 0
                    ? "COULD NOT LOAD GENRE DATA — TRY DISCONNECTING AND RECONNECTING SPOTIFY"
                    : $"NO GENRES TAGGED — SPOTIFY RETURNED {artistCount} ARTISTS WITH NO GENRE LABELS";
                sb.Append($"<p class=\"stat-empty\">{msg}</p>");
            }
            else
            {
                var maxCount = topGenres[0].Value;
                sb.Append("<div class=\"genre-chart\">");
                for (int i = 0; i < topGenres.Count; i++)
                {
                    var (genre, count) = (topGenres[i].Key, topGenres[i].Value);
                    var pct = (int)((double)count / maxCount * 100);
                    sb.Append("<div class=\"genre-bar-row\">");
                    sb.Append($"<span class=\"genre-bar-rank\">{i + 1:00}</span>");
                    sb.Append($"<span class=\"genre-bar-label\">{WebUtility.HtmlEncode(genre)}</span>");
                    sb.Append($"<div class=\"genre-bar-track\"><div class=\"genre-bar-fill\" style=\"--bw:{pct}%\"></div></div>");
                    sb.Append("</div>");
                }
                sb.Append("</div>");
            }
        }

        return Content(sb.ToString(), "text/html");
    }

    public async Task<IActionResult> OnGetRecentlyPlayedAsync()
    {
        var token = await GetValidTokenAsync();
        if (string.IsNullOrEmpty(token)) return Content("", "text/html");

        var client = _http.CreateClient();
        var req    = new HttpRequestMessage(HttpMethod.Get,
            "https://api.spotify.com/v1/me/player/recently-played?limit=20");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode)
                return Content("<p class=\"stat-empty\">COULD NOT LOAD RECENT PLAYS</p>", "text/html");

            var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var sb  = new StringBuilder("<div class=\"stat-list\">");
            int i   = 0;

            if (doc.RootElement.TryGetProperty("items", out var items))
                foreach (var item in items.EnumerateArray().Take(20))
                {
                    var trackName  = "";
                    var artistName = "";
                    var playedAt   = "";
                    var trackId    = "";

                    if (item.TryGetProperty("track", out var track))
                    {
                        trackName  = track.TryGetProperty("name",    out var tn) ? tn.GetString() ?? "" : "";
                        artistName = track.TryGetProperty("artists", out var ar) && ar.GetArrayLength() > 0 &&
                                     ar[0].TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                        trackId    = track.TryGetProperty("id", out var tid) ? tid.GetString() ?? "" : "";
                    }

                    if (item.TryGetProperty("played_at", out var pa) &&
                        DateTime.TryParse(pa.GetString(), out var dt))
                        playedAt = dt.ToLocalTime().ToString("MMM d, h:mm tt");

                    sb.Append("<div class=\"stat-row\">");
                    if (!string.IsNullOrEmpty(trackId))
                        sb.Append($"<button class=\"preview-btn\" data-trackid=\"{WebUtility.HtmlEncode(trackId)}\" onclick=\"qdPreview(this)\" title=\"PREVIEW\">&#9654;</button>");
                    sb.Append($"<span class=\"stat-rank\">{++i:00}</span>");
                    sb.Append("<div class=\"stat-info\">");
                    sb.Append($"<span class=\"stat-name\">{WebUtility.HtmlEncode(trackName)}</span>");
                    sb.Append($"<span class=\"stat-sub\">{WebUtility.HtmlEncode(artistName)}</span>");
                    sb.Append("</div>");
                    sb.Append($"<span class=\"stat-time\">{WebUtility.HtmlEncode(playedAt)}</span>");
                    sb.Append("</div>");
                }

            sb.Append("</div>");
            return Content(sb.ToString(), "text/html");
        }
        catch { return Content("<p class=\"stat-empty\">COULD NOT LOAD RECENT PLAYS</p>", "text/html"); }
    }

    public async Task<IActionResult> OnGetCreatePlaylistAsync(string? timeRange)
    {
        var token = await GetValidTokenAsync();
        if (string.IsNullOrEmpty(token))
            return Content("<span class=\"playlist-error\">NOT CONNECTED</span>", "text/html");

        var key  = $"SpotifyTrackUris_{timeRange ?? "6months"}";
        var uris = JsonSerializer.Deserialize<List<string>>(HttpContext.Session.GetString(key) ?? "[]") ?? new();
        if (uris.Count == 0)
            return Content("<span class=\"playlist-error\">VIEW TOP TRACKS FIRST TO ENABLE PLAYLIST CREATION</span>", "text/html");

        var client = _http.CreateClient();
        async Task<JsonDocument?> Fetch(string url)
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;
            return JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        }

        var profile = await Fetch("https://api.spotify.com/v1/me");
        var userId  = profile?.RootElement.TryGetProperty("id", out var idEl) == true ? idEl.GetString() ?? "" : "";
        if (string.IsNullOrEmpty(userId))
            return Content("<span class=\"playlist-error\">COULD NOT GET USER PROFILE</span>", "text/html");

        var label     = timeRange switch { "4weeks" => "4 Weeks", "12months" => "12 Months", _ => "6 Months" };
        var createReq = new HttpRequestMessage(HttpMethod.Post, $"https://api.spotify.com/v1/users/{userId}/playlists");
        createReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        createReq.Content = new StringContent(
            JsonSerializer.Serialize(new { name = $"My Top Tracks — Last {label}", description = "Created by QUEUEDUP", @public = false }),
            Encoding.UTF8, "application/json");

        var createResp = await client.SendAsync(createReq);
        if (!createResp.IsSuccessStatusCode)
            return Content("<span class=\"playlist-error\">FAILED TO CREATE PLAYLIST</span>", "text/html");

        var createDoc   = JsonDocument.Parse(await createResp.Content.ReadAsStringAsync());
        var playlistId  = createDoc.RootElement.TryGetProperty("id", out var pid) ? pid.GetString() ?? "" : "";
        var playlistUrl = createDoc.RootElement.TryGetProperty("external_urls", out var eu) &&
                          eu.TryGetProperty("spotify", out var su) ? su.GetString() ?? "" : "";

        var addReq = new HttpRequestMessage(HttpMethod.Post, $"https://api.spotify.com/v1/playlists/{playlistId}/tracks");
        addReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        addReq.Content = new StringContent(JsonSerializer.Serialize(new { uris }), Encoding.UTF8, "application/json");
        await client.SendAsync(addReq);

        return Content($"<a href=\"{WebUtility.HtmlEncode(playlistUrl)}\" target=\"_blank\" class=\"playlist-success\">PLAYLIST CREATED — OPEN IN SPOTIFY ↗</a>", "text/html");
    }

    public async Task<IActionResult> OnGetCheckAvailabilityAsync(string eventId)
    {
        var apiKey = _config["Ticketmaster:ApiKey"];
        var client = _http.CreateClient();

        string statusCode = "unknown";
        string eventName  = "";
        string venue = "", address = "", city = "", state = "", country = "";
        string ticketLimit = "";
        var priceRows = new List<(string Type, string Min, string Max, string Currency)>();

        try
        {
            var json = await client.GetStringAsync(
                $"https://app.ticketmaster.com/discovery/v2/events/{eventId}.json?apikey={apiKey}");
            var root = JsonDocument.Parse(json).RootElement;

            if (root.TryGetProperty("name", out var nm)) eventName = nm.GetString() ?? "";

            if (root.TryGetProperty("dates", out var dates) &&
                dates.TryGetProperty("status", out var st) &&
                st.TryGetProperty("code", out var code))
                statusCode = code.GetString() ?? "unknown";

            if (root.TryGetProperty("_embedded", out var emb) &&
                emb.TryGetProperty("venues", out var venues) &&
                venues.GetArrayLength() > 0)
            {
                var v = venues[0];
                if (v.TryGetProperty("name",    out var vn)) venue   = vn.GetString() ?? "";
                if (v.TryGetProperty("address", out var ad) && ad.TryGetProperty("line1", out var l1)) address = l1.GetString() ?? "";
                if (v.TryGetProperty("city",    out var ci) && ci.TryGetProperty("name",  out var cn)) city    = cn.GetString() ?? "";
                if (v.TryGetProperty("state",   out var s)  && s.TryGetProperty("name",   out var sn)) state   = sn.GetString() ?? "";
                if (v.TryGetProperty("country", out var co) && co.TryGetProperty("name",  out var con)) country = con.GetString() ?? "";
            }

            if (root.TryGetProperty("priceRanges", out var prices))
                foreach (var p in prices.EnumerateArray())
                    priceRows.Add((
                        Type:     Capitalize(p.TryGetProperty("type",     out var t)  ? t.GetString()  ?? "Standard" : "Standard"),
                        Min:      (p.TryGetProperty("min", out var mn) ? mn.GetDouble() : 0).ToString("N2"),
                        Max:      (p.TryGetProperty("max", out var mx) ? mx.GetDouble() : 0).ToString("N2"),
                        Currency: p.TryGetProperty("currency", out var c) ? c.GetString() ?? "USD" : "USD"
                    ));

            if (root.TryGetProperty("ticketLimit", out var tl) && tl.TryGetProperty("info", out var tli))
                ticketLimit = tli.GetString() ?? "";
        }
        catch { }

        var (statusText, statusClass) = statusCode switch
        {
            "onsale"      => ("AVAILABLE",                 "available"),
            "offsale"     => ("SOLD OUT",                  "soldout"),
            "cancelled"   => ("CANCELLED",                 "cancelled"),
            "rescheduled" => ("RESCHEDULED — CHECK DATES", "rescheduled"),
            _             => ("STATUS UNKNOWN",            "unknown"),
        };

        var locationParts = new[] { address, city, state, country }.Where(p => !string.IsNullOrWhiteSpace(p));
        var locationStr   = string.Join(", ", locationParts);

        var priceHtml = new StringBuilder();
        priceHtml.AppendLine("<span class=\"status-label\" style=\"margin-top:1rem\">TICKET TYPES</span>");
        if (priceRows.Count > 0)
        {
            priceHtml.AppendLine("<div class=\"ticket-types\">");
            foreach (var (type, min, max, cur) in priceRows)
                priceHtml.AppendLine($"<div class=\"ticket-row\"><span class=\"ticket-type\">{WebUtility.HtmlEncode(type)}</span><span class=\"ticket-price\">{cur} {min} &mdash; {max}</span></div>");
            priceHtml.AppendLine("</div>");
        }
        else
            priceHtml.AppendLine("<span class=\"no-price\">PRICE INFO NOT AVAILABLE FOR THIS EVENT</span>");

        var locationHtml = string.IsNullOrWhiteSpace(locationStr) ? "" : $"""
            <div class="status-location">
              <span class="status-label" style="margin-top:1rem">LOCATION</span>
              <span class="location-venue">{WebUtility.HtmlEncode(venue)}</span>
              <span class="location-addr">{WebUtility.HtmlEncode(locationStr)}</span>
            </div>
            """;

        var limitHtml = string.IsNullOrWhiteSpace(ticketLimit) ? "" :
            $"<span class=\"ticket-limit\">{WebUtility.HtmlEncode(ticketLimit)}</span>";

        var html = $"""
            <div class="status-card status-{statusClass}">
              <span class="status-label">AVAILABILITY STATUS</span>
              <span class="status-value">{statusText}</span>
              <span class="status-event">{WebUtility.HtmlEncode(eventName)}</span>
              {locationHtml}
              {priceHtml}
              {limitHtml}
            </div>
            """;

        return Content(html, "text/html");
    }

    private async Task<List<EventViewModel>> FetchEventsAsync(string? keyword, string? city)
    {
        var apiKey  = _config["Ticketmaster:ApiKey"];
        var client  = _http.CreateClient();
        var results = new List<EventViewModel>();

        var kw  = string.IsNullOrWhiteSpace(keyword) ? "" : $"&keyword={Uri.EscapeDataString(keyword)}";
        var ct  = string.IsNullOrWhiteSpace(city)    ? "" : $"&city={Uri.EscapeDataString(city)}";
        var url = $"https://app.ticketmaster.com/discovery/v2/events.json" +
                  $"?classificationName=music&size=50&sort=relevance,desc{kw}{ct}&apikey={apiKey}";

        try
        {
            var json = await client.GetStringAsync(url);
            var doc  = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("_embedded", out var embedded) ||
                !embedded.TryGetProperty("events", out var eventsArray))
                return results;

            foreach (var ev in eventsArray.EnumerateArray())
            {
                var vm = new EventViewModel
                {
                    Id        = ev.TryGetProperty("id",   out var id) ? id.GetString() ?? "" : "",
                    Name      = ev.TryGetProperty("name", out var nm) ? nm.GetString() ?? "" : "",
                    TicketUrl = ev.TryGetProperty("url",  out var tu) ? tu.GetString() ?? "" : "",
                };

                if (ev.TryGetProperty("dates", out var dates))
                {
                    if (dates.TryGetProperty("start",  out var start) && start.TryGetProperty("localDate", out var ld)) vm.Date       = ld.GetString() ?? "";
                    if (dates.TryGetProperty("status", out var status) && status.TryGetProperty("code",    out var cd)) vm.StatusCode = cd.GetString() ?? "";
                }

                if (ev.TryGetProperty("images", out var images))
                {
                    int    bestWidth = 0;
                    string fallback  = "";
                    foreach (var img in images.EnumerateArray())
                    {
                        if (!img.TryGetProperty("url", out var imgUrl)) continue;
                        var u = imgUrl.GetString() ?? "";
                        if (fallback == "") fallback = u;
                        bool is16x9 = img.TryGetProperty("ratio", out var ratio) && ratio.GetString() == "16_9";
                        int  width  = img.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                        if (is16x9 && width > bestWidth) { bestWidth = width; vm.ImageUrl = u; }
                    }
                    if (string.IsNullOrEmpty(vm.ImageUrl)) vm.ImageUrl = fallback;
                }

                if (ev.TryGetProperty("_embedded", out var evEmb) &&
                    evEmb.TryGetProperty("venues", out var venues) &&
                    venues.GetArrayLength() > 0)
                {
                    var v = venues[0];
                    if (v.TryGetProperty("name",    out var vn)) vm.Venue   = vn.GetString() ?? "";
                    if (v.TryGetProperty("address", out var ad) && ad.TryGetProperty("line1", out var l1)) vm.Address = l1.GetString() ?? "";
                    if (v.TryGetProperty("city",    out var ci) && ci.TryGetProperty("name",  out var cn)) vm.City    = cn.GetString() ?? "";
                    if (v.TryGetProperty("state",   out var s)  && s.TryGetProperty("name",   out var sn)) vm.State   = sn.GetString() ?? "";
                    if (v.TryGetProperty("country", out var co) && co.TryGetProperty("name",  out var con)) vm.Country = con.GetString() ?? "";
                }

                if (ev.TryGetProperty("priceRanges", out var prices) && prices.GetArrayLength() > 0)
                {
                    var p   = prices[0];
                    var cur = p.TryGetProperty("currency", out var c)  ? c.GetString()  ?? "USD" : "USD";
                    var min = p.TryGetProperty("min",      out var mn) ? mn.GetDouble() : 0;
                    var max = p.TryGetProperty("max",      out var mx) ? mx.GetDouble() : 0;
                    vm.PriceRange = $"{cur} {min:N0} – {max:N0}";
                }

                if (results.Count < 6 && !results.Any(e => e.Name == vm.Name))
                    results.Add(vm);
                if (results.Count >= 6) break;
            }
        }
        catch { }

        return results;
    }

    public async Task<IActionResult> OnGetNowPlayingAsync()
    {
        var token = await GetValidTokenAsync();
        if (string.IsNullOrEmpty(token)) return Content("", "text/html");

        var client = _http.CreateClient();
        var req    = new HttpRequestMessage(HttpMethod.Get, "https://api.spotify.com/v1/me/player/currently-playing");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await client.SendAsync(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.NoContent || !resp.IsSuccessStatusCode)
                return Content("", "text/html");

            var body = await resp.Content.ReadAsStringAsync();
            if (string.IsNullOrWhiteSpace(body)) return Content("", "text/html");

            var doc       = JsonDocument.Parse(body);
            var isPlaying = doc.RootElement.TryGetProperty("is_playing", out var ip) && ip.GetBoolean();
            if (!isPlaying) return Content("", "text/html");

            string trackName = "", artistName = "", albumArt = "", trackUrl = "";
            int progressMs = 0, durationMs = 0;

            if (doc.RootElement.TryGetProperty("item", out var item))
            {
                trackName = item.TryGetProperty("name", out var tn) ? tn.GetString() ?? "" : "";
                if (item.TryGetProperty("artists", out var ar) && ar.GetArrayLength() > 0)
                    artistName = ar[0].TryGetProperty("name", out var an) ? an.GetString() ?? "" : "";
                if (item.TryGetProperty("album", out var album) &&
                    album.TryGetProperty("images", out var imgs) && imgs.GetArrayLength() > 0)
                    albumArt = imgs[imgs.GetArrayLength() - 1].TryGetProperty("url", out var au) ? au.GetString() ?? "" : "";
                if (item.TryGetProperty("external_urls", out var eu) && eu.TryGetProperty("spotify", out var su))
                    trackUrl = su.GetString() ?? "";
            }
            if (doc.RootElement.TryGetProperty("progress_ms", out var pm)) progressMs = pm.GetInt32();
            if (doc.RootElement.TryGetProperty("item",        out var it2) &&
                it2.TryGetProperty("duration_ms", out var dm)) durationMs = dm.GetInt32();

            var pct     = durationMs > 0 ? (int)((double)progressMs / durationMs * 100) : 0;
            var artHtml = string.IsNullOrEmpty(albumArt) ? "" :
                $"<img class=\"np-art\" src=\"{WebUtility.HtmlEncode(albumArt)}\" alt=\"\" />";
            var trackHtml = string.IsNullOrEmpty(trackUrl)
                ? $"<span class=\"np-track\">{WebUtility.HtmlEncode(trackName)}</span>"
                : $"<a class=\"np-track\" href=\"{WebUtility.HtmlEncode(trackUrl)}\" target=\"_blank\">{WebUtility.HtmlEncode(trackName)}</a>";

            return Content($"""
                <div class="np-pulse"></div>
                <span class="np-label">NOW PLAYING</span>
                {artHtml}
                <div class="np-info">{trackHtml}<span class="np-artist">{WebUtility.HtmlEncode(artistName)}</span></div>
                <div class="np-bar-wrap"><div class="np-bar-fill" style="width:{pct}%"></div></div>
                """, "text/html");
        }
        catch { return Content("", "text/html"); }
    }

    public async Task<IActionResult> OnGetSignupAsync(string? email)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@') || !email.Contains('.'))
            return Content("<span class=\"signup-msg signup-error\">ENTER A VALID EMAIL ADDRESS</span>", "text/html");

        email = email.Trim().ToLower();

        if (await _db.EmailSignups.AnyAsync(e => e.Email == email))
            return Content("<span class=\"signup-msg signup-success\">YOU'RE ALREADY ON THE LIST ✓</span>", "text/html");

        _db.EmailSignups.Add(new EmailSignup { Email = email, SignedUpAt = DateTime.UtcNow });
        await _db.SaveChangesAsync();

        return Content("<span class=\"signup-msg signup-success\">YOU'RE ON THE LIST — WE'LL BE IN TOUCH ✓</span>", "text/html");
    }

    private async Task<List<string>> FetchSpotifyTopArtistsAsync(string token)
    {
        var client = _http.CreateClient();
        var req    = new HttpRequestMessage(HttpMethod.Get,
            "https://api.spotify.com/v1/me/top/artists?limit=8&time_range=medium_term");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        try
        {
            var resp = await client.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return new List<string>();

            var doc     = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
            var artists = new List<string>();
            if (doc.RootElement.TryGetProperty("items", out var items))
                foreach (var item in items.EnumerateArray())
                    if (item.TryGetProperty("name", out var name))
                        artists.Add(name.GetString() ?? "");
            return artists;
        }
        catch { return new List<string>(); }
    }

    public async Task<IActionResult> OnGetAuraAsync()
    {
        var token = await GetValidTokenAsync();
        if (string.IsNullOrEmpty(token))
            return Content("<p class=\"stat-empty\">CONNECT SPOTIFY TO SEE YOUR AURA</p>", "text/html");

        var client = _http.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        async Task<JsonDocument?> Fetch(string url)
        {
            try
            {
                var r = await client.GetAsync(url);
                if (!r.IsSuccessStatusCode) return null;
                return JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            }
            catch { return null; }
        }

        var tracksDoc = await Fetch("https://api.spotify.com/v1/me/top/tracks?limit=20&time_range=medium_term");
        var trackIds  = new List<string>();
        if (tracksDoc?.RootElement.TryGetProperty("items", out var tItems) == true)
            foreach (var t in tItems.EnumerateArray())
                if (t.TryGetProperty("id", out var tid))
                    trackIds.Add(tid.GetString() ?? "");

        double avgEnergy = 0.5, avgValence = 0.5, avgDance = 0.5, avgAcoustic = 0.3, avgTempo = 120;
        bool audioFeaturesWorked = false;
        if (trackIds.Count > 0)
        {
            var featDoc = await Fetch($"https://api.spotify.com/v1/audio-features?ids={string.Join(",", trackIds.Take(20))}");
            if (featDoc?.RootElement.TryGetProperty("audio_features", out var feats) == true)
            {
                var valid = feats.EnumerateArray().Where(f => f.ValueKind != JsonValueKind.Null).ToList();
                if (valid.Count > 0)
                {
                    audioFeaturesWorked = true;
                    double Avg(string p) => valid.Where(f => f.TryGetProperty(p, out _))
                        .Select(f => f.GetProperty(p).GetDouble()).DefaultIfEmpty(0).Average();
                    avgEnergy   = Avg("energy");
                    avgValence  = Avg("valence");
                    avgDance    = Avg("danceability");
                    avgAcoustic = Avg("acousticness");
                    avgTempo    = Avg("tempo");
                }
            }
        }

        var genreSet      = new HashSet<string>();
        var topArtists    = new List<string>();
        var auraArtistIds = new List<string>();
        foreach (var range in new[] { "medium_term", "short_term", "long_term" })
        {
            var aDoc = await Fetch($"https://api.spotify.com/v1/me/top/artists?limit=50&time_range={range}");
            if (aDoc?.RootElement.TryGetProperty("items", out var aItems2) != true) continue;
            foreach (var a in aItems2.EnumerateArray())
            {
                if (a.TryGetProperty("name", out var an) && topArtists.Count < 4)
                    topArtists.Add(an.GetString() ?? "");
                if (a.TryGetProperty("id", out var aid))
                { var id = aid.GetString(); if (!string.IsNullOrEmpty(id) && !auraArtistIds.Contains(id)) auraArtistIds.Add(id); }
                if (a.TryGetProperty("genres", out var gs))
                    foreach (var g in gs.EnumerateArray())
                    { var gn = g.GetString()?.ToLower() ?? ""; if (!string.IsNullOrEmpty(gn)) genreSet.Add(gn); }
            }
            if (genreSet.Count > 0) break;
        }

        if (genreSet.Count == 0 && auraArtistIds.Count > 0)
        {
            foreach (var batch in auraArtistIds.Distinct().Take(50).Chunk(50))
            {
                var d2 = await Fetch($"https://api.spotify.com/v1/artists?ids={string.Join(",", batch)}");
                if (d2?.RootElement.TryGetProperty("artists", out var arts) != true) continue;
                foreach (var a in arts.EnumerateArray())
                    if (a.TryGetProperty("genres", out var gs))
                        foreach (var g in gs.EnumerateArray())
                        { var gn = g.GetString()?.ToLower() ?? ""; if (!string.IsNullOrEmpty(gn)) genreSet.Add(gn); }
            }
        }

        bool HasG(params string[] terms) => genreSet.Any(g => terms.Any(t => g.Contains(t)));

        if (!audioFeaturesWorked)
        {
            if      (HasG("metal","hardcore","thrash","death","black metal","deathcore","metalcore")) { avgEnergy=0.92; avgValence=0.18; avgDance=0.28; avgAcoustic=0.04; }
            else if (HasG("punk","grunge","screamo","noise"))                                          { avgEnergy=0.85; avgValence=0.32; avgDance=0.38; avgAcoustic=0.08; }
            else if (HasG("edm","electronic","techno","house","dubstep","trance","drum and bass","dnb","hardstyle")) { avgEnergy=0.87; avgValence=0.68; avgDance=0.85; avgAcoustic=0.04; }
            else if (HasG("hip hop","hip-hop","rap","trap","drill","phonk","melodic rap"))             { avgEnergy=0.74; avgValence=0.55; avgDance=0.80; avgAcoustic=0.09; }
            else if (HasG("r&b","rnb","soul","neo soul","trap soul","quiet storm","motown"))           { avgEnergy=0.55; avgValence=0.65; avgDance=0.72; avgAcoustic=0.22; }
            else if (HasG("funk","disco","groove","nu-disco"))                                         { avgEnergy=0.78; avgValence=0.88; avgDance=0.88; avgAcoustic=0.10; }
            else if (HasG("latin","reggaeton","salsa","bachata","cumbia"))                             { avgEnergy=0.80; avgValence=0.80; avgDance=0.84; avgAcoustic=0.13; }
            else if (HasG("pop","dance pop","k-pop","j-pop","hyperpop","electropop","teen pop"))       { avgEnergy=0.72; avgValence=0.80; avgDance=0.78; avgAcoustic=0.15; }
            else if (HasG("indie","alternative","emo","shoegaze","dream pop","bedroom pop","post-punk")) { avgEnergy=0.55; avgValence=0.38; avgDance=0.48; avgAcoustic=0.35; }
            else if (HasG("rock","alternative rock","indie rock","classic rock","hard rock"))          { avgEnergy=0.78; avgValence=0.48; avgDance=0.48; avgAcoustic=0.12; }
            else if (HasG("ambient","lo-fi","lofi","chillhop","study","meditation","new age"))         { avgEnergy=0.22; avgValence=0.55; avgDance=0.44; avgAcoustic=0.52; }
            else if (HasG("folk","acoustic","singer-songwriter","americana","bluegrass"))               { avgEnergy=0.40; avgValence=0.62; avgDance=0.46; avgAcoustic=0.76; }
            else if (HasG("country","outlaw country","country pop"))                                    { avgEnergy=0.55; avgValence=0.70; avgDance=0.55; avgAcoustic=0.58; }
            else if (HasG("jazz","bebop","swing","bossa","fusion","smooth jazz"))                      { avgEnergy=0.45; avgValence=0.60; avgDance=0.56; avgAcoustic=0.58; }
            else if (HasG("blues","electric blues","delta blues"))                                      { avgEnergy=0.48; avgValence=0.32; avgDance=0.50; avgAcoustic=0.55; }
            else if (HasG("classical","orchestral","baroque","neoclassical","piano","chamber"))        { avgEnergy=0.22; avgValence=0.50; avgDance=0.28; avgAcoustic=0.72; }
        }

        double[] fv    = { avgEnergy, avgValence, avgDance, avgAcoustic };
        double   fmean = fv.Average();
        double   chaos = Math.Sqrt(fv.Select(v => (v - fmean) * (v - fmean)).Average());

        var sc = new Dictionary<string, double>
        {
            ["midnight"] = (1 - avgEnergy) * 2 + avgAcoustic * 2 + (1 - avgValence) * 2,
            ["neon"]     = avgEnergy * 3 + avgDance * 2,
            ["golden"]   = avgValence * 2 + (avgEnergy is > 0.3 and < 0.7 ? 1.5 : 0),
            ["chaos"]    = chaos * 10,
            ["vintage"]  = avgAcoustic * 2 + (avgTempo < 100 ? 1.5 : 0) + (1 - avgDance),
            ["storm"]    = avgEnergy * 2 + (1 - avgValence) * 3,
            ["minimal"]  = (1 - avgEnergy) * 3 + (1 - avgDance) * 2,
            ["social"]   = avgValence * 3 + avgDance * 2,
        };

        double boost = audioFeaturesWorked ? 2.0 : 5.0;
        if (HasG("indie","alternative","emo","bedroom","sad","shoegaze","dream pop","darkwave","goth","post-punk","emo rap","midwest emo","sad rap","slowcore")) sc["midnight"] += boost;
        if (HasG("edm","electronic","dance","techno","house","dubstep","trance","bass","dnb","drum and bass","big room","hardstyle","future bass","club","rave"))  sc["neon"]     += boost;
        if (HasG("r&b","rnb","soul","neo soul","chill","smooth","funk","motown","quiet storm","contemporary r&b","trap soul","groove","neo-soul"))                 sc["golden"]   += boost;
        if (HasG("pop","dance pop","viral","teen pop","k-pop","j-pop","hyperpop","electropop","bubblegum","idol","boy band","girl group","synth pop"))             sc["social"]   += boost;
        if (HasG("classic","oldies","vintage","jazz","folk","country","bluegrass","swing","blues","bossa","americana","singer-songwriter","acoustic","standards")) sc["vintage"]  += boost;
        if (HasG("metal","punk","hardcore","grunge","screamo","thrash","death","black metal","drill","nu metal","metalcore","deathcore","aggressive","noise"))     sc["storm"]    += boost;
        if (HasG("ambient","instrumental","lo-fi","lofi","study","sleep","meditation","new age","neoclassical","piano","classical","post-classical","chillhop"))   sc["minimal"]  += boost;
        if (HasG("hip hop","hip-hop","rap","trap","melodic rap","mumble rap","phonk","afrobeats","afropop","uk rap","cloud rap"))                                  sc["neon"]     += boost * 0.6;
        if (HasG("hip hop","hip-hop","rap","trap","melodic rap","latin","reggaeton","salsa","cumbia","bachata"))                                                   sc["social"]   += boost * 0.5;

        var key = sc.OrderByDescending(kv => kv.Value).First().Key;

        var defs = new Dictionary<string, (string Name, string Line, string Desc, string Grad, string Accent)>
        {
            ["midnight"] = ("MIDNIGHT DREAMER",
                "You don&rsquo;t just listen to music &mdash; you emotionally attach to it.",
                "Indie, alt, slow, sad. Feels everything deeply. Your playlist is basically a diary.",
                "135deg, #0d0221 0%, #1a0533 50%, #0f172a 100%", "#a78bfa"),
            ["neon"] = ("NEON PULSE",
                "If it doesn&rsquo;t make you move, it&rsquo;s getting skipped.",
                "EDM, hype, pop, rap. Main character energy. You need the drop.",
                "135deg, #050520 0%, #0a1040 50%, #001830 100%", "#00d2ff"),
            ["golden"] = ("GOLDEN HOUR SOUL",
                "Your life feels like a soft sunset filter.",
                "R&amp;B, chill pop, lo-fi. Calm, aesthetic, vibey. Probably has good taste in everything.",
                "135deg, #1a0a00 0%, #3d1c00 50%, #7c4d00 100%", "#f59e0b"),
            ["chaos"] = ("CHAOS CONTROLLER",
                "Your playlist has no rules &mdash; and neither do you.",
                "Everything. Literally everything. You could go from classical to death metal in one skip.",
                "135deg, #0a0a0a 0%, #1a0a2e 33%, #0a1a0a 66%, #2e0a0a 100%", "#a3e635"),
            ["vintage"] = ("VINTAGE HEART",
                "You belong in a different era.",
                "Oldies, classics, acoustic. You&rsquo;ve probably said &ldquo;they don&rsquo;t make music like this anymore.&rdquo;",
                "135deg, #1a0e05 0%, #3d1c08 50%, #5c2d10 100%", "#d97706"),
            ["storm"] = ("EMOTIONAL STORM",
                "You either feel nothing or everything at once.",
                "Sad + aggressive mix. High energy, low mood. Your playlist is a warning sign.",
                "135deg, #1a0000 0%, #3d0000 50%, #1a0010 100%", "#f43f5e"),
            ["minimal"] = ("MINIMALIST MIND",
                "You use music to escape, not to feel.",
                "Instrumental, lo-fi, ambient. Focused, quiet. Noise is the enemy.",
                "135deg, #080808 0%, #111111 50%, #1c1c1c 100%", "#94a3b8"),
            ["social"] = ("SOCIAL BUTTERFLY",
                "Your playlist is basically a party invite.",
                "Popular hits, dance, pop. Outgoing. You&rsquo;ve probably sent someone a song with zero context.",
                "135deg, #1a0015 0%, #3d0030 50%, #1a0020 100%", "#f472b6"),
        };

        var (aname, aline, adesc, agrad, aaccent) = defs[key];
        int pE = (int)(avgEnergy  * 100);
        int pV = (int)(avgValence * 100);
        int pD = (int)(avgDance   * 100);
        int pA = (int)(avgAcoustic* 100);

        var sb2 = new StringBuilder();
        sb2.Append($@"<div class=""aura-card"" style=""background:linear-gradient({agrad});--aura-accent:{aaccent};"">
  <div class=""aura-eyebrow"">MUSIC AURA</div>
  <h2 class=""aura-name"">{aname}</h2>
  <p class=""aura-line"">{aline}</p>
  <p class=""aura-desc"">{adesc}</p>
  <div class=""aura-bars"">
    <div class=""aura-bar-row""><span class=""aura-bar-label"">ENERGY</span><div class=""aura-bar-track""><div class=""aura-bar-fill"" style=""--bw:{pE}%""></div></div><span class=""aura-bar-val"">{pE}</span></div>
    <div class=""aura-bar-row""><span class=""aura-bar-label"">VIBE</span><div class=""aura-bar-track""><div class=""aura-bar-fill"" style=""--bw:{pV}%""></div></div><span class=""aura-bar-val"">{pV}</span></div>
    <div class=""aura-bar-row""><span class=""aura-bar-label"">DANCEABILITY</span><div class=""aura-bar-track""><div class=""aura-bar-fill"" style=""--bw:{pD}%""></div></div><span class=""aura-bar-val"">{pD}</span></div>
    <div class=""aura-bar-row""><span class=""aura-bar-label"">ACOUSTIC</span><div class=""aura-bar-track""><div class=""aura-bar-fill"" style=""--bw:{pA}%""></div></div><span class=""aura-bar-val"">{pA}</span></div>
  </div>");

        if (topArtists.Any())
        {
            sb2.Append(@"<div class=""aura-artists""><span class=""aura-artists-label"">TOP ARTISTS</span><div class=""aura-artist-chips"">");
            foreach (var a in topArtists)
                sb2.Append($@"<span class=""aura-artist-chip"">{WebUtility.HtmlEncode(a)}</span>");
            sb2.Append("</div></div>");
        }

        var rawLine   = aline.Replace("&rsquo;", "'").Replace("&mdash;", "—")
                             .Replace("&amp;", "&").Replace("&ldquo;", "\"").Replace("&rdquo;", "\"");
        var shareText = $"My music aura is {aname}. {rawLine} — analyzed by QUEUEDUP";
        sb2.Append($@"<button class=""aura-share-btn"" data-share=""{WebUtility.HtmlEncode(shareText)}"" onclick=""qdCopyAura(this)"">COPY AURA</button>");
        sb2.Append("</div>");

        return Content(sb2.ToString(), "text/html");
    }

    private static string? MapGenre(string raw)
    {
        var g = raw.ToLower();
        if (GM(g,"hip hop","hip-hop","hiphop","rap","melodic rap","boom bap","mumble rap","trap rap","conscious hip hop","southern hip hop","gangsta rap","uk rap","australian hip hop","pop rap")) return "HIP-HOP / RAP";
        if (GM(g,"trap","phonk","drill","cloud rap","emo rap","plugg","sad rap","pluggnb"))                                   return "TRAP";
        if (GM(g,"grime","uk garage","garage","bassline","speed garage"))                                                    return "GRIME / GARAGE";
        if (GM(g,"house","tech house","deep house","progressive house","electro house","nu disco","afro house","g-house","microhouse")) return "HOUSE / TECHNO";
        if (GM(g,"techno","minimal techno","industrial","ebm","industrial techno"))                                           return "HOUSE / TECHNO";
        if (GM(g,"drum and bass","dnb","jungle","neurofunk","liquid funk","liquid dnb","halftime"))                           return "DRUM & BASS";
        if (GM(g,"dubstep","brostep","riddim","future bass","bass music","complextro","big room","hardstyle","wave"))         return "ELECTRONIC / EDM";
        if (GM(g,"synthwave","darkwave","dark wave","coldwave","cold wave","new wave","vaporwave","retrowave","outrun"))      return "ELECTRONIC / EDM";
        if (GM(g,"edm","electronic","electro","trance","psytrance","progressive trance","festival edm","rave"))               return "ELECTRONIC / EDM";
        if (GM(g,"metal","metalcore","deathcore","death metal","black metal","thrash","doom","nu metal","heavy metal","sludge metal","post-metal")) return "METAL";
        if (GM(g,"punk","hardcore","pop punk","post-hardcore","screamo","emo","grunge","noise rock","skate punk","street punk")) return "PUNK / GRUNGE";
        if (GM(g,"rock","alternative rock","indie rock","garage rock","psychedelic rock","classic rock","hard rock","post-rock","progressive rock","math rock","stoner rock")) return "ROCK";
        if (GM(g,"indie","alternative","shoegaze","dream pop","bedroom pop","lo-fi indie","art rock","post-punk","chamber pop","twee","chillwave","sadcore","slowcore","jangle pop")) return "INDIE / ALT";
        if (GM(g,"classical","orchestral","baroque","romantic","opera","symphony","chamber music","contemporary classical","neoclassical","post-classical","piano classical","minimalism")) return "CLASSICAL";
        if (GM(g,"jazz","bebop","swing","fusion","smooth jazz","bossa nova","latin jazz","nu jazz","contemporary jazz","cool jazz","big band")) return "JAZZ";
        if (GM(g,"ambient","lo-fi","lofi","chillhop","study","downtempo","new age","drone","meditation","sleep","healing","chill","nature")) return "AMBIENT / LO-FI";
        if (GM(g,"reggae","dancehall","ska","dub","roots reggae","reggae fusion","afrobeats","afrobeat","afropop","afrofusion","highlife")) return "REGGAE / AFROBEATS";
        if (GM(g,"folk","acoustic","singer-songwriter","americana","bluegrass","celtic","new folk","country folk","freak folk","indie folk")) return "ACOUSTIC / FOLK";
        if (GM(g,"country","outlaw country","country pop","country rock","honky tonk","alt-country","red dirt"))              return "COUNTRY";
        if (GM(g,"blues","electric blues","delta blues","chicago blues","soul blues","texas blues"))                          return "BLUES";
        if (GM(g,"r&b","rnb","rhythm and blues","soul","neo soul","motown","quiet storm","contemporary r&b","trap soul","funk soul","alternative r&b")) return "R&B / SOUL";
        if (GM(g,"funk","disco","nu-disco","boogie","p-funk","funk rock","afrofunk","dance rock","post-disco"))               return "FUNK / DISCO";
        if (GM(g,"latin","reggaeton","salsa","bachata","cumbia","bossa","samba","latin pop","latin rock","latin hip hop","latin trap","tropical","merengue","corrido","norteno","banda","norteño")) return "LATIN";
        if (GM(g,"pop","dance pop","teen pop","k-pop","j-pop","c-pop","hyperpop","synth pop","electropop","bubblegum","idol","boy band","girl group","viral pop","adult contemporary")) return "POP";
        return null;
    }

    private static bool GM(string g, params string[] terms) => terms.Any(t => g.Contains(t));

    private static string RenderChange(int change)
    {
        if (change == int.MinValue) return "<span class=\"stat-change new\">NEW</span>";
        if (change > 0) return $"<span class=\"stat-change up\">▲{change}</span>";
        if (change < 0) return $"<span class=\"stat-change down\">▼{Math.Abs(change)}</span>";
        return "<span class=\"stat-change same\">—</span>";
    }

    private static int GetRankChange(List<string> prevList, string name, int currentIndex)
    {
        if (prevList.Count == 0) return int.MinValue;
        var prevIndex = prevList.IndexOf(name);
        if (prevIndex < 0) return int.MinValue;
        return prevIndex - currentIndex;
    }

    private static string Capitalize(string s) =>
        string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s[1..];
}
