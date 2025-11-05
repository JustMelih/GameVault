using System.Net.Http.Json;
using Microsoft.Extensions.Caching.Memory;

namespace GameVault.Integrations.Rawg;

public interface IRawgClient
{
    Task<List<RawgGame>> SearchAsync(string query, int limit, CancellationToken ct);
}

public class RawgClient : IRawgClient
{
    private readonly HttpClient _http;
    private readonly IMemoryCache _cache;
    private readonly IConfiguration _cfg;

    public RawgClient(HttpClient http, IMemoryCache cache, IConfiguration cfg)
    {
        _http = http; _cache = cache; _cfg = cfg;
    }

    public async Task<List<RawgGame>> SearchAsync(string query, int limit, CancellationToken ct)
    {
        // 1) Basic guard
        query = (query ?? "").Trim();
        if (string.IsNullOrEmpty(query)) return new List<RawgGame>();

        // 2) Cache key (same query → same results for 5 min)
        var cacheKey = $"rawg:{query}:{limit}";
        if (_cache.TryGetValue(cacheKey, out List<RawgGame> cached)) return cached;

        // 3) Build URL
        var key = _cfg["Rawg:ApiKey"];
        var size = Math.Clamp(limit * 2, 20, 40);
        var url = $"games?search={Uri.EscapeDataString(query)}&page_size={size}&key={key}";

        // 4) Call RAWG
        var resp = await _http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
        {
            // return empty on fail (we don’t crash the site)
            return new List<RawgGame>();
        }

        var dto = await resp.Content.ReadFromJsonAsync<RawgSearchResponse>(cancellationToken: ct);

        var list = (dto?.results ?? new()).Select(r => new RawgGame
        {
            Id = r.id,
            Name = r.name ?? "Unknown",
            Released = r.released,
            Platforms = r.platforms?.Select(p => p.platform?.name ?? "")
                         .Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new(),
            Genres = r.genres?.Select(g => g.name ?? "")
                       .Where(s => !string.IsNullOrWhiteSpace(s)).ToList() ?? new(),

            // 🔹 Yeni eklenen alanlar (mapleme)
            RatingsCount = r.ratings_count,
            Metacritic = r.metacritic
        }).ToList();


        // 5) Cache for 5 minutes
        _cache.Set(cacheKey, list, TimeSpan.FromMinutes(5));

        return list;
    }
}

// --- minimal RAWG DTOs (only fields we use) ---
public class RawgSearchResponse
{
    public List<RawgResult> results { get; set; } = new();
}
public class RawgResult
{
    public int id { get; set; }
    public string? name { get; set; }
    public string? released { get; set; }
    public int ratings_count { get; set; }
    public int? metacritic { get; set; }
    public List<RawgPlatformWrap>? platforms { get; set; }
    public List<RawgNameObj>? genres { get; set; }
}
public class RawgPlatformWrap { public RawgNameObj? platform { get; set; } }
public class RawgNameObj { public string? name { get; set; } }

// our simplified model for the controller
public class RawgGame
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? Released { get; set; }
    public int RatingsCount { get; set; }
    public int? Metacritic { get; set; }
    public List<string> Platforms { get; set; } = new();
    public List<string> Genres { get; set; } = new();
}
