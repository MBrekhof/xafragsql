using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace XafRag.Blazor.Server.Services;

public sealed record RerankScores(double[] Nouls, int InputTokens, int OutputTokens, TimeSpan Elapsed);

/// <summary>
/// Scores (question, passage) pairs with TypeSafe (https://docs.typesafe.ai/api): one Noul request
/// per passage, as in their re-ranking cookbook. There is no C# SDK, so this is the HTTP call.
/// </summary>
public class TypeSafeReranker
{
    public const string Endpoint = "https://api.typesafe.ai/v1/systemone";
    public const string Model = "jev-latest";
    private const int MaxConcurrency = 8;
    private const int MaxAttempts = 3;

    // The question is the same for every pair; the pair itself goes into `state`.
    private static readonly object Question = new
    {
        type = "noul",
        instructions = "Does `passage` contain information that helps answer `question`?",
        criteria = new
        {
            @true = "The passage states facts that directly answer the question or a necessary part of it.",
            @false = "The passage is only on a related topic, or does not address what the question asks.",
        },
    };

    private readonly HttpClient _http;

    public TypeSafeReranker(HttpClient http) => _http = http;

    /// <summary>One noul per passage, in input order. Throws if any passage could not be scored.</summary>
    public async Task<RerankScores> ScoreAsync(
        string question, IReadOnlyList<string> passages, string apiKey, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var gate = new SemaphoreSlim(MaxConcurrency);
        var results = await Task.WhenAll(passages.Select(async passage =>
        {
            await gate.WaitAsync(ct);
            try { return await ScoreOneAsync(question, passage, apiKey, ct); }
            finally { gate.Release(); }
        }));
        return new RerankScores(
            results.Select(r => r.Noul).ToArray(),
            results.Sum(r => r.InputTokens),
            results.Sum(r => r.OutputTokens),
            sw.Elapsed);
    }

    private async Task<(double Noul, int InputTokens, int OutputTokens)> ScoreOneAsync(
        string question, string passage, string apiKey, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
            {
                Content = JsonContent.Create(new
                {
                    state = new { question, passage },
                    model = Model,
                    questions = new { relevant = Question },
                }),
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);

            using var response = await _http.SendAsync(request, ct);
            // 429 rate limited, 529 overloaded: the documented retry-with-backoff cases.
            if ((int)response.StatusCode is 429 or 529 && attempt < MaxAttempts)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500 * (1 << (attempt - 1))), ct);
                continue;
            }
            response.EnsureSuccessStatusCode();

            using var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
            var root = doc.RootElement;
            var usage = root.GetProperty("usage");
            return (
                root.GetProperty("answers").GetProperty("relevant").GetProperty("noul").GetDouble(),
                usage.TryGetProperty("input_tokens", out var input) && input.ValueKind == JsonValueKind.Number ? input.GetInt32() : 0,
                usage.TryGetProperty("output_tokens", out var output) && output.ValueKind == JsonValueKind.Number ? output.GetInt32() : 0);
        }
    }
}
