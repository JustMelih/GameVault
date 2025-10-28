using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace GameVault.Controllers
{
    [ApiController] // API davranışlarını otomatikleştirir. (Çeşitli hatalarda 400,401,403 gibi kodların dönmesini sağlayarak anlamamıza yardımcı olur.)
    [Route("api/[controller]")] // URL kalıbı. ( "/api/ai" vb.)
    public class AiController : ControllerBase
    {
        private readonly IRawgClient _rawg;
        private readonly IIntentService _intent;            // ⚠ DI Lifetimes altında tekrar açıklanacak. 
        private readonly IMemoryCache _cache;
        private readonly ILogger<AiController> _logger;

        public AiController(IRawgClient rawg, IIntentService intent, IMemoryCache cache, ILogger<AiController> logger)  // CONSTRUCTOR ve "CONSTRUCTOR INJECTION"
        {
            _rawg = rawg;
            _intent = intent;    // " ⚠ DEPENDENCY INJECTION ⚠ " ile dışarıdan gelenleri değiştirilemez (readonly) olarak tanımlıyoruz. Tekrar açıklanacak.
            _cache = cache;
            _logger = logger;
        }

        [HttpPost("search")] // POST /api/ai/search. enpoint
        public async Task<IActionResult> Search([FromBody] SearchRequest req, CancellationToken ct) // ⚠ CancellationToken zinciri. 
        {
            if (string.IsNullOrWhiteSpace(req?.Query))
                return BadRequest(new { error = "Query cannot be empty." });

            // Tiny per-IP throttle: 5 req / 10s
            var clientKey = $"throttle:{HttpContext.Connection.RemoteIpAddress}";
            if (!TryConsumeToken(clientKey, 5, 10))
                return StatusCode(429, new { error = "Too many requests. Please wait a few seconds." });

            var sw = System.Diagnostics.Stopwatch.StartNew();
            var limit = req.Limit <= 0 ? 10 : Math.Min(req.Limit, 20);

            // ---- INTENT: cache -> LLM -> fallback (NO 503) ----
            ParsedIntent intent;
            var usedFallback = false;
            string? llmError = null;
            var intentKey = "intent:" + req.Query.Trim().ToLowerInvariant();

            if (!_cache.TryGetValue(intentKey, out intent!))
            {
                try
                {
                    intent = await _intent.ParseAsync(req.Query, ct);
                }
                catch (Exception ex)
                {
                    // Whatever the LLM error is, we DO NOT die. We fallback.
                    _logger.LogWarning(ex, "LLM failed for '{Query}', using heuristic fallback.", req.Query);
                    llmError = ex.Message;
                    intent = FallbackIntent(req.Query);
                    usedFallback = true;
                }

                intent.Include ??= new(); intent.Exclude ??= new(); intent.Titles ??= new();
                // Cache only if LLM succeeded
                if (!usedFallback) _cache.Set(intentKey, intent, TimeSpan.FromMinutes(30));
            }

            var include = intent.Include!;
            var exclude = intent.Exclude!;
            var titles = intent.Titles!
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .Distinct(StringComparer.InvariantCultureIgnoreCase)
                .Take(usedFallback ? 0 : 2) // skip RAWG hint spam if we’re on fallback
                .ToList();

            // ---- RAWG main query (NEVER 503 on failure) ----
            var rawgQuery = string.Join(' ', new[] { req.Query, string.Join(' ', include) }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

            List<RawgGame> mainList;
            string? rawgError = null;
            try
            {
                mainList = await _rawg.SearchAsync(rawgQuery, limit, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "RAWG main query failed for '{RawgQuery}'", rawgQuery);
                rawgError = ex.Message;
                mainList = new(); // keep going with empty list
            }

            // ---- RAWG hint queries (bounded, and only if not fallback) ----
            var hintResults = new List<RawgGame>();
            if (titles.Count > 0)
            {
                try
                {
                    var tasks = titles.Select(t => _rawg.SearchAsync(t, 5, ct)).ToList();
                    var lists = await Task.WhenAll(tasks);
                    foreach (var l in lists) hintResults.AddRange(l);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "RAWG hint search failed; continuing with main results.");
                    rawgError ??= "hint search failed";
                }
            }

            // ---- Merge + de-duplicate ----
            var all = mainList.Concat(hintResults)
                              .GroupBy(g => g.Id)
                              .Select(g => g.First())
                              .ToList();
            // collapse near-duplicate names (Dream, Dreams, Dreams.)
            all = all
                .GroupBy(g => g.Name.Trim().ToLowerInvariant().TrimEnd('.', '!', '?'))
                .Select(g => g.First())
                .ToList();

            // ---- Negative filters ----
            var filtered = ApplyNegatives(all, exclude).ToList();

            // ---- Ranking ----
            int Rank(RawgGame g)
            {
                var name = (g?.Name ?? "").ToLowerInvariant();

                // exact blockbuster matches
                foreach (var t in titles)
                {
                    var tt = t.ToLowerInvariant();
                    if (name == tt) return 0;
                    if (name.StartsWith(tt)) return 1;
                    if (name.Contains(tt)) return 2;
                }

                // include keyword presence
                var hay = ((g?.Name ?? "") + " " + string.Join(' ', g?.Genres ?? new List<string>())).ToLowerInvariant();
                if (include.Any(k => !string.IsNullOrWhiteSpace(k) && hay.Contains(k.ToLowerInvariant())))
                    return 3;

                // ↓↓↓ add platform penalty here (before fallback)
                var goodPlatform = (g?.Platforms ?? new List<string>()).Any(p =>
                    p.Contains("PC", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("PlayStation", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Xbox", StringComparison.OrdinalIgnoreCase) ||
                    p.Contains("Nintendo", StringComparison.OrdinalIgnoreCase)
                );
                if (!goodPlatform)
                    return 8; // sink browser/mobile-only shovelware

                // penalty for generic single-word names (cheap / vague)
                if (name.Length < 6 && !name.Contains("3") && !name.Contains("ii") && !name.Contains("iv"))
                    return 7;

                // fallback
                return 6;
            }

            var ranked = filtered.OrderBy(g => Rank(g))
                                 .ThenBy(g => g.Name)
                                 .Take(limit);

            var items = ranked.Select(g => new
            {
                title = g.Name,
                why = BuildWhy(g, intent)
            });

            return Ok(new
            {
                items,
                tookMs = (int)sw.ElapsedMilliseconds,
                debug = new
                {
                    rawgQuery,
                    include,
                    exclude,
                    titles,
                    llmFallback = usedFallback,
                    llmError,
                    rawgError
                }
            });
        }

        // -------- helpers --------
        private static ParsedIntent FallbackIntent(string q)
        {
            var text = (q ?? "").ToLowerInvariant();
            var inc = new List<string>();
            var exc = new List<string>();

            if (text.Contains("ortaçağ") || text.Contains("orta cag") || text.Contains("medieval")) inc.Add("medieval");
            if (text.Contains("ejder") || text.Contains("ejderha") || text.Contains("dragon")) inc.Add("dragon");
            if (text.Contains("uzay") || text.Contains("space") || text.Contains("sci-fi")) { inc.Add("space"); inc.Add("sci-fi"); }
            if (text.Contains("korku") || text.Contains("horror")) inc.Add("horror");
            if (text.Contains("rpg")) inc.Add("rpg");
            if (text.Contains("açık dünya") || text.Contains("acik dunya") || text.Contains("open world")) inc.Add("open world");
            if (text.Contains("polis") || text.Contains("police")) inc.Add("police");
            if (text.Contains("koval") || text.Contains("chase")) inc.Add("chase");
            if (text.Contains("yarış") || text.Contains("yaris") || text.Contains("race") || text.Contains("racing")) inc.Add("racing");

            if (text.Contains("büyü olmasın") || text.Contains("buyu olmasin") || text.Contains("büyüsüz") || text.Contains("no magic"))
                exc.Add("magic");
            if (text.Contains("sihir") || text.Contains("büyü")) exc.Add("magic");
            if (text.Contains("sihirbaz") || text.Contains("wizard")) exc.Add("wizard");

            return new ParsedIntent
            {
                Include = inc.Distinct().ToList(),
                Exclude = exc.Distinct().ToList(),
                Titles = new List<string>()
            };
        }

        private IEnumerable<RawgGame> ApplyNegatives(IEnumerable<RawgGame> list, List<string> excludes)
        {
            if (list == null) yield break;

            var excludesNorm = (excludes ?? new()).Where(s => !string.IsNullOrWhiteSpace(s))
                                                  .Select(s => s.ToLowerInvariant())
                                                  .ToList();

            if (excludesNorm.Count == 0)
            {
                foreach (var g in list) yield return g;
                yield break;
            }

            var forbidMap = new Dictionary<string, string[]>
            {
                ["magic"] = new[] { "magic", "mage", "wizard", "sorcerer" },
                ["wizard"] = new[] { "wizard", "mage", "sorcerer", "magic" }
            };

            foreach (var g in list)
            {
                var haystack = $"{g?.Name ?? ""} {string.Join(' ', g?.Genres ?? new())} {string.Join(' ', g?.Platforms ?? new())}"
                    .ToLowerInvariant();

                var blocked = excludesNorm.Any(ex =>
                    (forbidMap.TryGetValue(ex, out var words) && words.Any(w => haystack.Contains(w)))
                    || haystack.Contains(ex));

                if (!blocked) yield return g;
            }
        }

        private string[] BuildWhy(RawgGame g, ParsedIntent intent)
        {
            var hints = new List<string>();

            var inc = intent?.Include ?? new();
            if (inc.Count > 0)
                hints.Add("Matched: " + string.Join(", ", inc.Take(2)));

            var genres = g?.Genres ?? new List<string>();
            if (genres.Count > 0)
                hints.Add("Genres: " + string.Join(", ", genres.Take(2)));

            var plats = g?.Platforms ?? new List<string>();
            if (plats.Count > 0)
                hints.Add("Platforms: " + string.Join(", ", plats.Take(2)));

            var rel = g?.Released;
            if (!string.IsNullOrWhiteSpace(rel))
                hints.Add("Release: " + rel);

            return hints.Take(3).ToArray();
        }

        public class SearchRequest
        {
            public string? Query { get; set; }
            public int Limit { get; set; } = 10;
        }

        private bool TryConsumeToken(string key, int tokensPer10s, int windowSeconds)
        {
            var now = DateTimeOffset.UtcNow;
            var windowKey = $"{key}:{now.ToUnixTimeSeconds() / windowSeconds}";

            if (!_cache.TryGetValue(windowKey, out int count))
                count = 0;

            if (count >= tokensPer10s) return false;

            _cache.Set(windowKey, count + 1, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(windowSeconds)
            });
            return true;
        }
    }
}
