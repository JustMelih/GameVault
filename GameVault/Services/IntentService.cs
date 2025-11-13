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

    // === STRONGLY-TYPED DTO'lar ===
    private sealed record ChatReq(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("messages")] IEnumerable<ChatMsg> Messages,
        [property: JsonPropertyName("temperature")] double Temperature
    );

    private sealed record ChatMsg(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    private sealed record ChatRes(
        [property: JsonPropertyName("choices")] List<Choice> Choices
    );

    private sealed record Choice(
        [property: JsonPropertyName("message")] Msg Message
    );

    private sealed record Msg(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    public async Task<IReadOnlyList<string>> GetTitleCandidatesAsync(string userQuery, CancellationToken ct)
    {
        var model = _cfg["LLM:Model"] ?? "gpt-4o-mini";

        var sys = new ChatMsg("system",
            "Return exactly 10 canonical BASE GAME titles that best match the user request. " +
            "No DLCs, no remasters/definitive/complete/bundle/collection. " +
            "Output format: a SINGLE LINE, titles separated by \" - \". No extra text.");
        var usr = new ChatMsg("user", userQuery);

        var req = new ChatReq(model, new[] { sys, usr }, 1);

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(req)
        };

        // Per-request Authorization (en sağlıklısı)
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
            res.EnsureSuccessStatusCode(); // exception fırlat
        }

        // Güvenli deser (case-insensitive)
        var body = JsonSerializer.Deserialize<ChatRes>(bodyText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var content = body?.Choices?.Count > 0 ? body!.Choices[0].Message.Content : null;
        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("OpenAI returned empty content for query '{Query}'. Raw: {Raw}", userQuery, bodyText);
            return Array.Empty<string>();
        }

        // Olası code block/quote kırpma (nadiren olur)
        content = content.Trim().Trim('`').Trim();

        var titles = content.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .Take(15)
                            .ToList();

        _logger.LogInformation("AI titles for '{Query}': {Titles}", userQuery, string.Join(" | ", titles));
        return titles;
    }
}
