using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

public interface IIntentService
{
    Task<ParsedIntent> ParseAsync(string userText, CancellationToken ct);
}

public sealed class IntentService : IIntentService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<IntentService> _logger;

    public IntentService(HttpClient http, IConfiguration cfg, ILogger<IntentService> logger)
    {
        _http = http;
        _cfg = cfg;
        _logger = logger;

        // Ensure base URL
        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri("https://api.openai.com/v1/");
    }

    public async Task<ParsedIntent> ParseAsync(string userText, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(userText))
            throw new ArgumentException("userText cannot be empty.", nameof(userText));

        // --- ONLY REQUIRE API KEY ---
        var apiKey = (_cfg["OPENAI_API_KEY"] ?? _cfg["LLM:ApiKey"])?.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("Missing OPENAI_API_KEY / LLM:ApiKey.");

        var model = _cfg["LLM:Model"] ?? "gpt-4o-mini";

        var messages = new object[]
        {
            new {
                role = "system",
                content =
        @"You convert a natural-language game query into STRICT JSON:
        {""include"":[], ""exclude"":[], ""titles"":[]}

        RULES:
        - include: 2–6 SHORT ENGLISH concept tokens (lowercase), e.g. ""medieval"", ""dragon"", ""open world"", ""horror"", ""police chase"", ""rpg"".
          * Translate non-English concepts to English (e.g., TR: ""ejderha"" → ""dragon"", ""ortaçağ"" → ""medieval"", ""açık dünya"" → ""open world"").
          * No punctuation, no phrases longer than 2–3 words.
        - exclude: 0–6 SHORT ENGLISH tokens to avoid (e.g., ""magic"", ""wizard"", ""multiplayer"", ""sci-fi"").
        - titles: 3–6 LIKELY, POPULAR ENGLISH game/franchise names that match the vibe, even if user did NOT name them.
          * Prefer globally known blockbusters first. Proper case.
          * If vague query (""the game where we kill monsters""), still guess iconic fits (e.g., The Witcher 3, Monster Hunter: World, DOOM).
        - Never echo the user's text, never add commentary. Output ONLY raw JSON (no code fences).
        - Dedupe all lists. Keep ""include/exclude"" lowercase; keep ""titles"" proper-cased."
            },

            // Few-shot 1: police chase
            new { role = "user", content = "police chase" },
            new { role = "assistant", content =
                @"{""include"":[""police"",""" + "chase" + @""",""racing""],""exclude"":[],""titles"":[""Grand Theft Auto V"",""Need for Speed: Hot Pursuit"",""Need for Speed: Most Wanted"",""Driver: San Francisco"",""Burnout Paradise Remastered""]}" },

            // Few-shot 2: vague monster killing
            new { role = "user", content = "the game where we kill monsters" },
            new { role = "assistant", content =
                @"{""include"":[""monster"",""combat"",""rpg""],""exclude"":[],""titles"":[""The Witcher 3: Wild Hunt"",""Monster Hunter: World"",""Dark Souls III"",""DOOM (2016)"",""Diablo III""]}" },

            // Few-shot 3: Turkish → English concepts, plus a negative
            new { role = "user", content = "ortaçağ ejderha var ama büyü olmasın, açık dünya olsun" },
            new { role = "assistant", content =
                @"{""include"":[""medieval"",""dragon"",""open world""],""exclude"":[""magic""],""titles"":[""The Elder Scrolls V: Skyrim"",""Dragon's Dogma: Dark Arisen"",""Kingdom Come: Deliverance"",""Dark Souls III""]}" },

            // Actual user message
            new { role = "user", content = userText }
        };

        var body = new
        {
            model,
            temperature = 0.1,              // more deterministic, fewer derps
            top_p = 0.9,                    // mild diversity
            messages,
            response_format = new { type = "json_object" }
        };


        using var req = new HttpRequestMessage(HttpMethod.Post, "chat/completions");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

        // 💀 NO PROJECT / ORG HEADERS AT ALL 💀
        req.Content = JsonContent.Create(body);

        using var resp = await _http.SendAsync(req, ct);
        var raw = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI returned {Status}: {Body}", resp.StatusCode, raw);
            throw new HttpRequestException($"OpenAI error {resp.StatusCode}: {raw}");
        }

        using var doc = JsonDocument.Parse(raw);
        var content = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "{}";

        var intent = JsonSerializer.Deserialize<ParsedIntent>(content, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new ParsedIntent();

        intent.Include ??= new();
        intent.Exclude ??= new();
        intent.Titles ??= new();

        return intent;
    }
}

public sealed class ParsedIntent
{
    [JsonPropertyName("include")] public List<string> Include { get; set; } = new();
    [JsonPropertyName("exclude")] public List<string> Exclude { get; set; } = new();
    [JsonPropertyName("titles")] public List<string> Titles { get; set; } = new();
}
