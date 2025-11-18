using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

public interface IGameSummaryService
{
    /// <summary>
    /// Her ResolvedGame için, ID => kısa açıklama sözlüğü döner.
    /// 1 oyun = max 1 cümle, en fazla 25 İngilizce kelime.
    /// </summary>
    Task<IReadOnlyDictionary<int, string>> SummarizeAsync(
        IReadOnlyList<ResolvedGame> games,
        CancellationToken ct);
}

public sealed class GameSummaryService : IGameSummaryService
{
    private readonly HttpClient _http;
    private readonly IConfiguration _cfg;
    private readonly ILogger<GameSummaryService> _logger;

    public GameSummaryService(HttpClient http, IConfiguration cfg, ILogger<GameSummaryService> logger)
    {
        _http = http;
        _cfg = cfg;
        _logger = logger;

        if (_http.BaseAddress is null)
        {
            var baseUrl = (_cfg["LLM:BaseUrl"] ?? "https://api.openai.com/v1/").TrimEnd('/') + "/";
            _http.BaseAddress = new Uri(baseUrl);
        }
    }

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
        [property: JsonPropertyName("totel_tokens")] int TotalTokens
    );

    private sealed record Choice(
        [property: JsonPropertyName("message")] Msg Message 
    );

    private sealed record Msg(
        [property: JsonPropertyName("role")] string Role,
        [property: JsonPropertyName("content")] string Content
    );

    public async Task<IReadOnlyDictionary<int, string>> SummarizeAsync(IReadOnlyList<ResolvedGame> games, CancellationToken ct)       
    {
        var candidates = games
            .Where(g => !string.IsNullOrWhiteSpace(g.DescriptionRaw) || !string.IsNullOrWhiteSpace(g.Name))
            .ToList();

        if (candidates.Count == 0)
            return new Dictionary<int, string>();

        var model = _cfg["LLM:Model"] ?? "gpt-5.1-mini";
        var systemPrompt = @"
                You write short store blurbs for video games.

                Rules:
                - One sentence per game, in English, max 20 words.
                - Focus on what the player does and the game's vibe.
                - Do NOT mention platforms, release dates, review scores, or ""this game is/you play as"" boilerplate.
                - Answer with one line per game: ID|summary
                ";

        var sb = new StringBuilder();

        foreach (var g in candidates)
        {
            var text = BuildCompactText(g);
            sb.AppendLine($"ID: {g.Id}");
            sb.AppendLine($"TEXT: {text}");
            sb.AppendLine("---");
        }

        var sys = new ChatMsg("system", systemPrompt);
        var usr = new ChatMsg("user", sb.ToString());

        var req = new ChatReq(
            Model: model,
            Messages: new[] {sys, usr },
            MaxCompletionTokens: 299
        );

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, "chat/completions")
        {
            Content = JsonContent.Create(req)
        };

        var apiKey = _cfg["LLM:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("LLM:ApiKey missing (user-secrets).");

        httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);

        var projectId = _cfg["LLM:ProjectId"];
        if (!string.IsNullOrWhiteSpace(projectId))
            httpReq.Headers.Add("OpenAI-Project", projectId);

        var res = await _http.SendAsync(httpReq, ct);
        var bodyText = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogError("Summary LLM error {Status}: {Body}", (int)res.StatusCode, bodyText);
            res.EnsureSuccessStatusCode();
        }

        var body = JsonSerializer.Deserialize<ChatRes>(bodyText, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        var content = body?.Choices?.Count > 0
            ? body!.Choices[0].Message.Content
            : null;

        if (body?.Usage is { } u)
        {
            _logger.LogInformation(
                "Summary LLM usage: prompt={Prompt}, completion={Comp}, total={Total}",
                u.PromptTokens, u.CompletionTokens, u.TotalTokens);
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            _logger.LogWarning("Summary LLM returned empty content. Raw: {Raw}", bodyText);
            return new Dictionary<int, string>();
        }

        // Olası ``` bloklarını kırp
        content = content.Trim().Trim('`').Trim();

        var dict = new Dictionary<int, string>();

        var lines = content.Split('\n',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var line in lines)
        {
            var l = line.Trim();
            if (string.IsNullOrWhiteSpace(l)) continue;

            var parts = l.Split('|', 2);
            if (parts.Length != 2) continue;

            if (!int.TryParse(parts[0].Trim(), out var id)) continue;

            var summary = parts[1].Trim();
            if (string.IsNullOrWhiteSpace(summary)) continue;

            dict[id] = summary;
        }

        return dict;
    }
    private static string BuildCompactText(ResolvedGame g)
    {
        // Önce isim
        var baseText = g.Name ?? string.Empty;

        // Sonra description'ı kırparak ekle
        var desc = g.DescriptionRaw;
        if (!string.IsNullOrWhiteSpace(desc))
        {
            desc = NormalizeSpaces(desc);

            // max 60 kelime / max 350 karakter gibi agresif bir limit
            desc = TrimWords(desc, maxWords: 60, maxChars: 350);
            if (!string.IsNullOrWhiteSpace(desc))
            {
                baseText = $"{baseText}. {desc}";
            }
        }
        else if (!string.IsNullOrWhiteSpace(g.GenresCsv))
        {
            // Description yoksa genres'tan ufak bir ipucu ver
            baseText = $"{baseText}. Genres: {g.GenresCsv}";
        }

        return baseText;
    }

    private static string NormalizeSpaces(string s)
    {
        s = s.Replace("\r", " ").Replace("\n", " ");
        while (s.Contains("  "))
            s = s.Replace("  ", " ");
        return s.Trim();
    }

    private static string TrimWords(string s, int maxWords, int maxChars)
    {
        var words = s.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length > maxWords)
            words = words.Take(maxWords).ToArray();

        var joined = string.Join(' ', words);
        if (joined.Length > maxChars)
            joined = joined[..maxChars];

        return joined;
    }
}