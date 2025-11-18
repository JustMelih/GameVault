using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public interface ITitleIntentService
{
    Task<IReadOnlyList<string>> GetTitleCandidatesAsync(string userQuery, CancellationToken ct);
}

public sealed class TitleIntentService : ITitleIntentService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TitleIntentService> _logger;

    public TitleIntentService(HttpClient http, IConfiguration cfg, ILogger<TitleIntentService> logger)
    {
        _http = http;
        _cfg = cfg;
        _logger = logger;

        if (_http.BaseAddress is null)
            _http.BaseAddress = new Uri((_cfg["LLM:BaseUrl"] ?? "https://api.openai.com/v1/").TrimEnd('/') + "/");
    }

    // ====================== DTO'lar ======================

    private sealed record ChatReq(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IEnumerable<ChatMsg> Messages,
        [property: JsonPropertyName("max_completion_tokens")] int? MaxCompletionTokens = null
    );

    private sealed record ChatMsg(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    private sealed record ChatRes(
        [property: JsonPropertyName("choices")] List<Choice> Choices,
        [property: JsonPropertyName("usage")] Usage? Usage
    );

    private sealed record Usage(
        [property: JsonPropertyName("prompt_tokens")] int PromptTokens,
        [property: JsonPropertyName("completion_tokens")] int CompletionTokens,
        [property: JsonPropertyName("total_tokens")] int TotalTokens
    );

    private sealed record Choice(
        [property: JsonPropertyName("message")] Msg Message
    );

    private sealed record Msg(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    // =====================================================

    public async Task<IReadOnlyList<string>> GetTitleCandidatesAsync(string userQuery, CancellationToken ct)
    {
        var model = _cfg["LLM:Model"] ?? "gpt-5-chat-latest"; // veya "gpt-5"

        // 🔥 Ultra kısaltılmış, sert system prompt
        var systemPrompt =
@"You are a video game search brain.

Rules:
- ONLY output REAL, EXISTING video game BASE TITLES.
- NO DLCs, NO expansions, NO remasters, NO ""Definitive/Complete/GOTY/Legendary/Anniversary Edition"" variants.
- If the query requires a mechanic (farming, football, racing, soulslike, border-checking/papers-please-like, etc.),
  ONLY include games that TRULY have that mechanic.
- If there are not enough good matches, return FEWER than 10 (do NOT fill with weak matches).
- If the user implies ""recent"", ""modern"", ""not too old"", prefer newer games and avoid very old ones.
- For big yearly franchises (FIFA, PES, Farming Simulator, etc.) prefer the most relevant / latest entries.

Output format:
- ONE SINGLE LINE.
- Format: Title 1 - Title 2 - Title 3 - ...
- NO numbering, NO bullets, NO comments, NO years, NO platforms, NO extra text.";

        var sys = new ChatMsg("system", systemPrompt);
        var usr = new ChatMsg("user", userQuery);

        var req = new ChatReq(
            Model: model,
            Messages: new[] { sys, usr },
            MaxCompletionTokens: 128
        );

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(req)
        };

        var apiKey = _cfg["LLM:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM:ApiKey missing (user-secrets).");

        httpReq.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var projectId = _cfg["LLM:ProjectId"];
        if (!string.IsNullOrWhiteSpace(projectId))
            httpReq.Headers.Add("OpenAI-Project", projectId);

        var res = await _http.SendAsync(httpReq, ct);
        var bodyText = await res.Content.ReadAsStringAsync(ct);

        if (!res.IsSuccessStatusCode)
        {
            _logger.LogError("OpenAI error {Status}: {Body}", (int)res.StatusCode, bodyText);
            res.EnsureSuccessStatusCode(); // exception fırlatır
        }

        var body = JsonSerializer.Deserialize<ChatRes>(bodyText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var content = body?.Choices?.Count > 0
            ? body!.Choices[0].Message.Content
            : null;

        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("OpenAI returned empty content for query '{Query}'. Raw: {Raw}", userQuery, bodyText);
            return Array.Empty<string>();
        }
        
        if (body?.Usage is { } u)
        {
            _logger.LogInformation(
                "LLM usage for query '{Query}': prompt={Prompt}, completion={Comp}, total={Total}",
                userQuery, u.PromptTokens, u.CompletionTokens, u.TotalTokens
                );
        }

        // Olası ``` ... ``` kırpmaları
        content = content.Trim().Trim('`').Trim();

        var titles = content
            .Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(20)
            .ToList();

        _logger.LogInformation("AI titles for '{Query}': {Titles}", userQuery, string.Join(" | ", titles));
        return titles;
    }
}
