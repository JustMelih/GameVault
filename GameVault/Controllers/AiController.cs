using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace GameVault.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AiController : ControllerBase
{
    private readonly ITitleIntentService _intent;   // AI sadece başlık listesi döndürür
    private readonly IRawgResolver _resolver;       // Başlığı RAWG’de çözüp oyun nesnesine çevirir
    private readonly IGameSummaryService _summaries; // AI: kısa açıklamalar
    private readonly ILogger<AiController> _logger;
    private readonly IMemoryCache _cache;

    public AiController(
        ITitleIntentService intent,
        IRawgResolver resolver,
        IMemoryCache cache,
        IGameSummaryService summaries,
        ILogger<AiController> logger)
    {
        _intent = intent;
        _resolver = resolver;
        _cache = cache;
        _summaries = summaries;
        _logger = logger;
    }

    public sealed record SearchRequest(string Query, string? Platform = null);
    public sealed record SearchResponse(string Query, IReadOnlyList<ResolvedGame> Results);

    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] SearchRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Query))
            return BadRequest(new { error = "Query cannot be empty." });

        var response = await ExecuteSearchAsync(req, ct);
        return Ok(response);
    }

    [HttpPost("search-cards")]
    public async Task<IActionResult> SearchCards([FromBody] SearchRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Query))
            return BadRequest(new { error = "Query cannot be empty." });

        var searchResponse = await ExecuteSearchAsync(req, ct);
        var games = searchResponse.Results;

        // 2.a) AI ile özetleri üret
        IReadOnlyDictionary<int, string>? summaries = null;
        try
        {
            summaries = await _summaries.SummarizeAsync(games, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Summary generation failed for query '{Query}'", req.Query);
        }

        // 2.b) DTO'ya map'le
        var cards = games
            .Select(g => ToDto(g, summaries))
            .ToList();

        return Ok(cards);
    }

    private async Task<SearchResponse> ExecuteSearchAsync(SearchRequest req, CancellationToken ct)
    {
        var normalizedQuery = req.Query.Trim().ToLowerInvariant();
        var normalizedPlatform = req.Platform?.Trim().ToLowerInvariant();
        var cacheKey = $"ai:v2:{normalizedQuery}:{normalizedPlatform}";

        if (_cache.TryGetValue(cacheKey, out SearchResponse? cached) && cached is not null)
            return cached;

        // 1) AI’dan kanonik başlık listesi
        var candidates = await _intent.GetTitleCandidatesAsync(req.Query, ct);
        if (candidates.Count == 0)
        {
            var emptyResp = new SearchResponse(req.Query, Array.Empty<ResolvedGame>());
            _cache.Set(cacheKey, emptyResp, TimeSpan.FromMinutes(5));
            return emptyResp;
        }

        // 2) Başlıkları RAWG’de çöz (paralel)
        var resolved = await _resolver.ResolveManyAsync(candidates, req.Platform, ct);

        // 3) Response + cache
        var response = new SearchResponse(req.Query, resolved);
        _cache.Set(cacheKey, response, TimeSpan.FromMinutes(5));
        return response;
    }

    private static AiGameResultDto ToDto(ResolvedGame g, IReadOnlyDictionary<int, string>? summaries)
    {
        string? year = null;
        if (!string.IsNullOrWhiteSpace(g.Released))
        {
            if (DateTime.TryParse(g.Released, out var d))
                year = d.Year.ToString();
            else if (g.Released!.Length >= 4)
                year = g.Released[..4];
        }

        var rawgUrl = !string.IsNullOrWhiteSpace(g.Slug)
            ? $"https://rawg.io/games/{g.Slug}"
            : null;

        // Summary: önce AI'dan geleni dene, yoksa RAWG'den kaba kes
        string? summary = null;

        if (summaries is not null &&
            summaries.TryGetValue(g.Id, out var s) &&
            !string.IsNullOrWhiteSpace(s))
        {
            summary = s;
        }
        else
        {
            summary = BuildShortDescription(g.DescriptionRaw, maxWords: 25);
        }

        return new AiGameResultDto
        {
            Title = g.Name,
            Year = year,
            Genres = g.GenresCsv,
            Metacritic = g.Metacritic,
            Summary = summary,
            ImageUrl = g.BackgroundImage,
            RawgUrl = rawgUrl
        };
    }

    private static string? BuildShortDescription(string? raw, int maxWords)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        // Satır sonlarını vs. boşluk yap
        raw = raw.Replace("\r", " ").Replace("\n", " ");

        var words = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length <= maxWords)
            return string.Join(' ', words);

        return string.Join(' ', words.Take(maxWords)) + "...";
    }
}

// Kart için DTO (aynı dosyada kalabilir, namespace aynı)
public sealed class AiGameResultDto
{
    public string Title { get; init; } = string.Empty;
    public string? Year { get; init; }
    public string? Genres { get; init; }
    public int? Metacritic { get; init; }
    public string? Summary { get; init; }
    public string? ImageUrl { get; init; }
    public string? RawgUrl { get; init; }
}
