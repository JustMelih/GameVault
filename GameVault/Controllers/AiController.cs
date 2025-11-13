using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace GameVault.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class AiController : ControllerBase
{
    private readonly ITitleIntentService _intent;   // AI sadece başlık listesi döndürür
    private readonly IRawgResolver _resolver;       // Başlığı RAWG’de çözüp oyun nesnesine çevirir
    private readonly ILogger<AiController> _logger;
    private readonly IMemoryCache _cache;

    public AiController(
        ITitleIntentService intent,
        IRawgResolver resolver,
        IMemoryCache cache,
        ILogger<AiController> logger)
    {
        _intent = intent;
        _resolver = resolver;
        _cache = cache;
        _logger = logger;
    }

    public sealed record SearchRequest(string Query, string? Platform = null);
    public sealed record SearchResponse(string Query, IReadOnlyList<ResolvedGame> Results);

    [HttpPost("search")]
    public async Task<IActionResult> Search([FromBody] SearchRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req?.Query))
            return BadRequest(new { error = "Query cannot be empty." });

        var cacheKey = $"ai:v2:{req.Query.Trim().ToLowerInvariant()}:{req.Platform?.ToLowerInvariant()}";
        if (_cache.TryGetValue(cacheKey, out SearchResponse? cached) && cached is not null)
            return Ok(cached);

        // 1) AI’dan kanonik başlık listesi
        var candidates = await _intent.GetTitleCandidatesAsync(req.Query, ct);
        if (candidates.Count == 0)
            return Ok(new SearchResponse(req.Query, Array.Empty<ResolvedGame>()));

        // 2) Başlıkları RAWG’de çöz (paralel)
        var resolved = await _resolver.ResolveManyAsync(candidates, req.Platform, ct);

        // 3) Boşsa: yine de döndür (şeffaflık)
        var response = new SearchResponse(req.Query, resolved);
        _cache.Set(cacheKey, response, TimeSpan.FromMinutes(5));
        return Ok(response);
    }
}
