using Humanizer.Localisation;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net.Http.Json;

public sealed record ResolvedGame(
    int Id,
    string Name,
    string? Released,
    string Slug,
    string? BackgroundImage,
    double Score,         // hafif deterministik skor (recency/popularity)
    string? PlatformsCsv,  // görüntü amaçlı
    int? Metacritic,       // kartta göstereceğiz
    string? GenresCsv,     // "RPG, Simulation" gibi
    string? DescriptionRaw // RAWG'den gelen ham description (AI özet için de kullanacağız)
);

public interface IRawgClient
{
    Task<IReadOnlyList<RawgGame>> SearchAsync(string title, CancellationToken ct);
    Task<RawgGame?> GetDetailsAsync(int id, CancellationToken ct);
}

public interface IRawgResolver
{
    Task<IReadOnlyList<ResolvedGame>> ResolveManyAsync(
        IReadOnlyList<string> titles, string? preferPlatform, CancellationToken ct);
}

public sealed class RawgClient : IRawgClient
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly ILogger<RawgClient> _logger;

    public RawgClient(HttpClient http, IConfiguration cfg, ILogger<RawgClient> logger)
    {
        _http = http;
        _logger = logger;
        _apiKey = cfg["RAWG:ApiKey"] ?? throw new InvalidOperationException("RAWG:ApiKey missing");
        _http.BaseAddress ??= new Uri("https://api.rawg.io/api/");
        _http.Timeout = TimeSpan.FromSeconds(5);
    }

    public async Task<IReadOnlyList<RawgGame>> SearchAsync(string title, CancellationToken ct)
    {
        var url = $"games?search={Uri.EscapeDataString(title)}&page_size=5&key={_apiKey}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await _http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();

        var json = await res.Content.ReadFromJsonAsync<RawgResponse>(cancellationToken: ct);
        return json?.Results ?? new List<RawgGame>();
    }
    public async Task<RawgGame?> GetDetailsAsync(int id, CancellationToken ct)
    {
        var url = $"games/{id}?key={_apiKey}";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var res = await _http.SendAsync(req, ct);

        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("RAWG details failed for id {Id}: {StatusCode}", id, res.StatusCode);
            return null;
        }

        var game = await res.Content.ReadFromJsonAsync<RawgGame>(cancellationToken: ct);
        return game;
    }
}

public sealed class RawgResponse 
{ 
    public List<RawgGame> Results { get; init; } = new();
}

public sealed class RawgGame
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
    public string Slug { get; init; } = "";
    public string? Released { get; init; }
    public string? Background_Image { get; init; }
    public int Additions_Count { get; init; }
    public int? Metacritic { get; init; }
    public List<ParentPlatform> Parent_Platforms { get; init; } = new();
    public List<Genre> Genres { get; init; } = new();     // detay ve liste endpointinde dolu gelebiliyor
    public string? Description_Raw { get; init; }          // sadece /games/{id} endpointinde dolu
}

public sealed class ParentPlatform 
{ 
    public Platform Platform { get; init; } = new(); 
}

public sealed class Platform 
{
    public string Slug { get; init; } = ""; 
}

public sealed class Genre
{
    public int Id { get; init; }
    public string Name { get; init; } = "";
}

public sealed class RawgResolver : IRawgResolver
{
    private readonly IRawgClient _rawg;
    private readonly ILogger<RawgResolver> _logger;

    public RawgResolver(IRawgClient rawg, ILogger<RawgResolver> logger)
    {
        _rawg = rawg;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ResolvedGame>> ResolveManyAsync(
        IReadOnlyList<string> titles, string? preferPlatform, CancellationToken ct)
    {
        var tasks = titles.Select(t => ResolveOneAsync(t, preferPlatform, ct));
        var results = await Task.WhenAll(tasks);

        var uniq = results.Where(x => x is not null)
                          .GroupBy(x => x!.Id)
                          .Select(g => g.First()!)
                          .OrderByDescending(x => x.Score)
                          .Take(10)
                          .ToList();
        return uniq;
    }

    private async Task<ResolvedGame?> ResolveOneAsync(string title, string? preferPlatform, CancellationToken ct)
    {
        try
        {
            var list = await _rawg.SearchAsync(title, ct);
            if (list.Count == 0) return null;

            var chosen = list.FirstOrDefault(Acceptable);
            chosen ??= list.First(); // hiçbiri geçmezse en üsttekini yine al

            RawgGame? details = null;
            try
            {
                details = await _rawg.GetDetailsAsync(chosen.Id, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "RAWG details threw for id {Id}", chosen.Id);
            }

            // details gelirse oradan, gelmezse chosen'dan oku
            var info = details ?? chosen;

            var score = Rank(chosen, preferPlatform);
            var platformsCsv = string.Join(",",
                chosen.Parent_Platforms.Select(p => p.Platform.Slug));

            string? genresCsv = null;
            if (info.Genres is { Count: > 0 })
            {
                genresCsv = string.Join(", ", info.Genres.Select(g => g.Name));
            }

            var descriptionRaw = info.Description_Raw;

            return new ResolvedGame(
                chosen.Id,
                chosen.Name,
                chosen.Released,
                chosen.Slug,
                chosen.Background_Image,
                score,
                platformsCsv,
                info.Metacritic,
                genresCsv,
                descriptionRaw
            );
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Resolve failed for title '{Title}'", title);
            return null;
        }
    }

    // DLC/Edition/Bundle/Collection hızlı eler
    private static bool Acceptable(RawgGame g)
    {
        if (string.IsNullOrWhiteSpace(g.Released)) return false;
        if (g.Additions_Count > 0) return false;
        var n = g.Name.ToLowerInvariant();
        if (n.Contains("dlc") || n.Contains("soundtrack") || n.Contains("definitive")
            || n.Contains("remaster") || n.Contains("remastered")
            || n.Contains("complete") || n.Contains("bundle") || n.Contains("collection"))
            return false;
        return true;
    }

    // Basit deterministik skor: yakın tarih + metacritic + tercih edilen platform kesişimi
    private static double Rank(RawgGame g, string? preferPlatform)
    {
        double s = 0;

        if (DateTime.TryParse(g.Released, out var d))
        {
            var ageDays = (DateTime.UtcNow - d.ToUniversalTime()).TotalDays;
            // daha yeniye hafif pozitif
            s += Math.Max(0, 1000 - Math.Min(1000, ageDays / 10.0));
        }

        if (g.Metacritic is int mc)
            s += mc * 2;

        if (!string.IsNullOrWhiteSpace(preferPlatform))
        {
            var hit = g.Parent_Platforms.Any(p => p.Platform.Slug.Equals(preferPlatform, StringComparison.OrdinalIgnoreCase));
            if (hit) s += 150;
        }

        return s;
    }
}
