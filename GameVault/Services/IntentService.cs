using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using Microsoft.Extensions.Logging;

public interface IIntentService
{
    Task<ParsedIntent> ParseAsync(string userText, CancellationToken ct);
    // Anladığım kadarıyla kullanıcının querysini al düzelt niyetini anla ve döndür diyor, Interface kullanarak bu işi yapacak her sınıfın bu metotlara uyması gerektiğini şartlıyoruz.
}

public sealed class IntentService : IIntentService // IntentService sınıfı, IntentService Interface'ine bağlı. Bu yüzden kesinlikle ParseAsync metodu olacak.
{  // 'sealed' çünkü kalıtım almasın. 
    private readonly HttpClient _http; // API ça
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
        @"Convert a natural-language game query into STRICT JSON:

        {""include"":[], ""exclude"":[], ""titles"":[]}

        RULES:
        - Output ONLY raw JSON (no code fences, no comments).
        - If unsure, leave fields empty. Never invent slugs/ids.
        - include: 2–6 short ENGLISH tokens (lowercase). Max 2–3 words per token. Examples: ""open world"", ""police chase"", ""horror"", ""rpg"", ""medieval"".
        - exclude: 0–6 short ENGLISH tokens (lowercase). Examples: ""magic"", ""multiplayer"", ""sci-fi"".
        - titles: 3–6 LIKELY, POPULAR ENGLISH game/franchise names (Proper Case). Prefer globally known blockbusters first.
        - Dedupe all lists."
            },

            // Few-shot 1
            new { role = "user", content = "police chase" },
            new { role = "assistant", content =
                @"{""include"":[""police"",""chase"",""racing""],""exclude"":[],""titles"":[""Grand Theft Auto V"",""Need for Speed: Hot Pursuit"",""Need for Speed: Most Wanted"",""Driver: San Francisco"",""Burnout Paradise Remastered""]}" },

            // Few-shot 2
            new { role = "user", content = "the game where we kill monsters" },
            new { role = "assistant", content =
                @"{""include"":[""monster"",""combat"",""rpg""],""exclude"":[],""titles"":[""The Witcher 3: Wild Hunt"",""Monster Hunter: World"",""Dark Souls III"",""DOOM (2016)"",""Diablo III""]}" },
            // Few-shot: "kill monsters for coin" → Witcher
            new { role = "user", content = "the game where we kill monsters for coin" },
            new { role = "assistant", content =
            @"{""include"":[""monster"",""bounty"",""contract"",""rpg""],""exclude"":[],""titles"":[""The Witcher 3: Wild Hunt"",""The Witcher 2: Assassins of Kings"",""Monster Hunter: World""]}" },

            // Few-shot 3 (Keanu → Cyberpunk)
            new { role = "user", content = "that game where we play in the future with keanu reeves" },
            new { role = "assistant", content =
                @"{""include"":[""keanu reeves"",""future"",""rpg""],""exclude"":[],""titles"":[""Cyberpunk 2077"",""Deus Ex: Human Revolution"",""The Ascent""]}" },
            // Few-shot: cowboy → RDR2
            new { role = "user", content = "that game where we play as a cowboy" },
            new { role = "assistant", content =
            @"{""include"":[""cowboy"",""western"",""open world""],""exclude"":[],""titles"":[""Red Dead Redemption 2"",""Red Dead Redemption"",""Call of Juarez: Gunslinger"",""Desperados III""]}" },

            // Actual query
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
