// RAG-003 spike: does TypeSafe re-ranking put better chunks into the prompt than cosine distance
// alone? Indexes the 10 sample documents into a separate database, runs the questions in
// rerank-eval.json both ways, and writes docs/rerank-eval-report.md.
//
// Run by hand (costs money, takes minutes; not part of dotnet test):
//   dotnet run --project XafRag/XafRag.RerankEval              evaluate (indexes on first run)
//   dotnet run --project XafRag/XafRag.RerankEval -- --dump    list the chunks, to check gold labels
//   dotnet run --project XafRag/XafRag.RerankEval -- --reingest
//
// Keys: OpenAI from the server's appsettings.Development.json, TypeSafe from the app's own
// database (the key an admin entered under Knowledge Base > Rerank Settings). Nothing to set here.
//
// Unlike RagService.SearchAsync there is no security filter here: the eval measures ranking, and
// its chunks have no parent rows. It uses the app's own vector query, rerank client and prompt.

using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using OpenAI;
using XafRag.Blazor.Server.Configuration;
using XafRag.Blazor.Server.Services;
using XafRag.Module.BusinessObjects;

// The pinned corpus: the sample documents, not the implementation guide next to them.
string[] corpus =
[
    "blazor-components.md", "blazor-fundamentals.md", "dark-forest-theory.md", "dotnet-rag-quickstart.md",
    "ef-core-getting-started.md", "ef-core-querying.md", "ef-core-relationships.md", "fermi-paradox.md",
    "xaf-crud-operations.md", "xaf-security-passwords.md",
];
// TypeSafe's published price for Jev input tokens (output is free): $0.042 per 1M, jev-1.12,
// docs.typesafe.ai re-ranking cookbook, "as of 2026-08".
const double TypeSafeUsdPerMillionInput = 0.042;

var root = FindRepoRoot();
var serverDir = Path.Combine(root, "XafRag", "XafRag.Blazor.Server");
var config = new ConfigurationBuilder()
    .AddJsonFile(Path.Combine(serverDir, "appsettings.json"))
    .AddJsonFile(Path.Combine(serverDir, "appsettings.Development.json"), optional: true)
    .Build();

var ragOptions = config.GetSection(RagOptions.SectionName).Get<RagOptions>()!;
var openAi = config.GetSection(OpenAiOptions.SectionName).Get<OpenAiOptions>()!;
var appConnection = config.GetConnectionString("ConnectionString")!;
var connection = new SqlConnectionStringBuilder(appConnection)
{
    InitialCatalog = "XafRagSQL_Eval",
}.ConnectionString;

await using var db = new RagDbContext(new DbContextOptionsBuilder<RagDbContext>().UseSqlServer(connection).Options);
await db.Database.MigrateAsync();

var openAiClient = new OpenAIClient(openAi.ApiKey);
var embedder = new EmbeddingService(
    openAiClient.GetEmbeddingClient(openAi.EmbeddingModel).AsIEmbeddingGenerator(),
    NullLogger<EmbeddingService>.Instance);
IChatClient chat = openAiClient.GetChatClient(openAi.ChatModel).AsIChatClient();

if (args.Contains("--reingest"))
    await db.KnowledgeChunks.ExecuteDeleteAsync();
if (!await db.KnowledgeChunks.AnyAsync())
{
    var chunker = new ChunkingService(Options.Create(ragOptions));
    for (var i = 0; i < corpus.Length; i++)
    {
        var chunks = chunker.ChunkText(await File.ReadAllTextAsync(Path.Combine(root, "docs", corpus[i])));
        var vectors = await embedder.GenerateEmbeddingsAsync(chunks.Select(c => c.Content).ToList());
        for (var c = 0; c < chunks.Count; c++)
        {
            db.KnowledgeChunks.Add(new KnowledgeChunk
            {
                Content = chunks[c].Content,
                Embedding = vectors[c],
                TokenCount = chunks[c].TokenCount,
                ChunkIndex = chunks[c].ChunkIndex,
                SourceType = ChunkSourceType.Article,
                KnowledgeArticleId = i + 1, // identity map into `corpus`; the eval DB has no parent tables
            });
        }
        Console.WriteLine($"indexed {corpus[i]}: {chunks.Count} chunks");
    }
    await db.SaveChangesAsync();
}

var allChunks = await db.KnowledgeChunks.OrderBy(c => c.KnowledgeArticleId).ThenBy(c => c.ChunkIndex).ToListAsync();
string FileOf(int? articleId) => corpus[articleId!.Value - 1];

var questions = JsonSerializer.Deserialize<QuestionSet>(
    await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "rerank-eval.json")),
    new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower })!.Questions;

if (args.Contains("--dump"))
{
    foreach (var c in allChunks)
        Console.WriteLine($"{FileOf(c.KnowledgeArticleId)}#{c.ChunkIndex} ({c.Content.Length} chars): {Flatten(c.Content)[..Math.Min(140, Flatten(c.Content).Length)]}");
    // Gold is per chunk: the phrase must land in a chunk of the gold file (overlap can put it in two).
    Console.WriteLine();
    foreach (var q in questions)
    {
        var hits = allChunks.Where(c => c.Content.Contains(q.GoldPhrase, StringComparison.Ordinal))
            .Select(c => $"{FileOf(c.KnowledgeArticleId)}#{c.ChunkIndex}").ToList();
        var ok = hits.Count > 0 && hits.All(h => h.StartsWith(q.GoldFile + "#"));
        Console.WriteLine($"{(ok ? "ok  " : "BAD ")} {q.Id} gold chunks: {string.Join(", ", hits)}");
    }
    return 0;
}

var typeSafeKey = await ReadAppTypeSafeKeyAsync(appConnection);
if (string.IsNullOrWhiteSpace(typeSafeKey))
{
    Console.Error.WriteLine("No TypeSafe key in the app's database. In the app: Knowledge Base > Rerank Settings > " +
                            "open the row > Set API Key > OK > Save. Then run this again.");
    return 1;
}

var reranker = new TypeSafeReranker(new HttpClient { Timeout = TimeSpan.FromSeconds(60) });
var outcomes = new List<Outcome>();
foreach (var q in questions)
{
    bool IsGold(string file, string content) => file == q.GoldFile && content.Contains(q.GoldPhrase, StringComparison.Ordinal);
    var goldInCorpus = allChunks.Count(c => IsGold(FileOf(c.KnowledgeArticleId), c.Content));

    var queryVector = await embedder.GenerateEmbeddingAsync(q.Question);
    var candidates = await RagService.VectorCandidates(db, queryVector, ragOptions).ToListAsync();
    foreach (var c in candidates)
        c.SourceName = FileOf(c.KnowledgeArticleId);

    var scores = await reranker.ScoreAsync(q.Question, candidates.Select(c => c.Content).ToList(), typeSafeKey, CancellationToken.None);
    // Scored without a deadline so latency is measured honestly, but judged like the app: past its
    // budget the chat keeps the distance order, so a slow rerank earns nothing here either.
    var reranked = scores.Elapsed > RagService.RerankBudget
        ? candidates
        : candidates.Select((c, i) => (c, i))
            .OrderByDescending(x => scores.Nouls[x.i]).ThenBy(x => x.i).Select(x => x.c).ToList();

    int? GoldRank(List<SearchResult> order)
    {
        var index = order.FindIndex(r => IsGold(r.SourceName, r.Content));
        return index < 0 ? null : index + 1;
    }

    async Task<(string Text, long Tokens)> AnswerAsync(List<SearchResult> order)
    {
        var response = await chat.GetResponseAsync(
        [
            new ChatMessage(ChatRole.System, RagService.BuildSystemMessage(order.Take(ragOptions.MaxResults).ToList())),
            new ChatMessage(ChatRole.User, q.Question),
        ]);
        return (response.Text, (response.Usage?.InputTokenCount ?? 0) + (response.Usage?.OutputTokenCount ?? 0));
    }

    var distanceAnswer = await AnswerAsync(candidates);
    var rerankAnswer = await AnswerAsync(reranked);
    var outcome = new Outcome(q, goldInCorpus, candidates.Count, GoldRank(candidates), GoldRank(reranked),
        scores, distanceAnswer.Text, rerankAnswer.Text, distanceAnswer.Tokens + rerankAnswer.Tokens,
        candidates.Take(ragOptions.MaxResults).Select(Label).ToList(), reranked.Take(ragOptions.MaxResults).Select(Label).ToList());
    outcomes.Add(outcome);
    Console.WriteLine($"{q.Id} {q.Kind,-12} distance #{outcome.DistanceRank?.ToString() ?? "-"}  rerank #{outcome.RerankRank?.ToString() ?? "-"}  ({scores.Elapsed.TotalMilliseconds:F0} ms)");

    string Label(SearchResult r) => $"{r.SourceName}#{r.ChunkIndex}{(IsGold(r.SourceName, r.Content) ? "*" : "")}";
}

var reportPath = Path.Combine(root, "docs", "rerank-eval-report.md");
await File.WriteAllTextAsync(reportPath, Report(outcomes, allChunks.Count, ragOptions, corpus.Length));
Console.WriteLine($"report: {reportPath}");
return 0;

string Report(List<Outcome> results, int chunkCount, RagOptions options, int fileCount)
{
    var inv = CultureInfo.InvariantCulture;
    var k = options.MaxResults;
    string Pct(Func<Outcome, bool> hit, IEnumerable<Outcome> set) =>
        set.Any() ? (100.0 * set.Count(hit) / set.Count()).ToString("F0", inv) + "%" : "-";
    string Mrr(Func<Outcome, int?> rank, IEnumerable<Outcome> set) =>
        set.Any() ? set.Average(o => rank(o) is { } r ? 1.0 / r : 0).ToString("F2", inv) : "-";

    var sb = new StringBuilder();
    sb.AppendLine("# Rerank evaluation (RAG-003)");
    sb.AppendLine();
    sb.AppendLine($"Generated {DateTime.Now:yyyy-MM-dd HH:mm} by `XafRag.RerankEval`. Corpus: {fileCount} sample documents, " +
                  $"{chunkCount} chunks. Each question gets the app's vector search (top {options.MaxResults * 4} candidates " +
                  $"under distance {options.DistanceThreshold.ToString(inv)}), then those candidates re-ordered by TypeSafe " +
                  $"(`{TypeSafeReranker.Model}`, one Noul per candidate). The prompt gets the top {k} of each order.");
    sb.AppendLine();
    sb.AppendLine($"**Small corpus:** {options.MaxResults * 4} candidates is a large share of {chunkCount} chunks, so this compares " +
                  "ranking within a shortlist; it says nothing about recall on a large knowledge base.");
    sb.AppendLine();
    sb.AppendLine("A *gold* chunk is one from the gold document that contains the gold phrase. A gold chunk outside the candidates counts as a miss.");
    sb.AppendLine();
    sb.AppendLine("| Set | n | gold in candidates | distance hit@1 | rerank hit@1 | distance hit@" + k + " | rerank hit@" + k + " | distance MRR | rerank MRR |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|---|");
    foreach (var (name, set) in new[] { ("all", results) }
                 .Concat(results.GroupBy(o => o.Question.Kind).OrderBy(g => g.Key).Select(g => (g.Key, g.ToList()))))
    {
        sb.AppendLine($"| {name} | {set.Count} | {Pct(o => o.DistanceRank != null, set)} " +
                      $"| {Pct(o => o.DistanceRank == 1, set)} | {Pct(o => o.RerankRank == 1, set)} " +
                      $"| {Pct(o => o.DistanceRank <= k, set)} | {Pct(o => o.RerankRank <= k, set)} " +
                      $"| {Mrr(o => o.DistanceRank, set)} | {Mrr(o => o.RerankRank, set)} |");
    }
    sb.AppendLine();

    var latencies = results.Select(o => o.Scores.Elapsed.TotalMilliseconds).OrderBy(x => x).ToList();
    var inputTokens = results.Sum(o => (long)o.Scores.InputTokens);
    var median = latencies.Count % 2 == 1
        ? latencies[latencies.Count / 2]
        : (latencies[latencies.Count / 2 - 1] + latencies[latencies.Count / 2]) / 2;
    var overBudget = results.Count(o => o.Scores.Elapsed > RagService.RerankBudget);
    sb.AppendLine($"**Rerank latency** per question (all candidates scored in parallel, max 8 in flight): " +
                  $"median {median:F0} ms, max {latencies[^1]:F0} ms. {overBudget} of {results.Count} exceeded the app's " +
                  $"{RagService.RerankBudget.TotalSeconds:F0} s budget; like the app, those keep the distance order in the rerank columns.");
    sb.AppendLine();
    sb.AppendLine($"**TypeSafe cost:** {inputTokens:N0} input + {results.Sum(o => (long)o.Scores.OutputTokens):N0} output tokens for " +
                  $"{results.Sum(o => o.Scores.Nouls.Length)} scored pairs = ${inputTokens / 1e6 * TypeSafeUsdPerMillionInput:F4} " +
                  $"(${TypeSafeUsdPerMillionInput} per 1M input tokens, TypeSafe's published jev-1.12 price) — about " +
                  $"${inputTokens / 1e6 * TypeSafeUsdPerMillionInput / results.Count:F5} per question. " +
                  $"OpenAI chat for the {results.Count * 2} answers: {results.Sum(o => o.ChatTokens):N0} tokens.");
    sb.AppendLine();

    var labelProblems = results.Where(o => o.GoldInCorpus == 0).ToList();
    if (labelProblems.Count > 0)
    {
        sb.AppendLine($"**Label problems:** {string.Join(", ", labelProblems.Select(o => o.Question.Id))} — gold phrase not found in any chunk of the gold file; these count as misses.");
        sb.AppendLine();
    }

    sb.AppendLine("## Per question");
    sb.AppendLine();
    sb.AppendLine($"Rank = position of the first gold chunk (- = not among the {options.MaxResults * 4} candidates). `*` marks gold chunks in the top {k}.");
    sb.AppendLine();
    sb.AppendLine("| id | kind | question | gold | distance rank | rerank rank | top " + k + " distance | top " + k + " rerank |");
    sb.AppendLine("|---|---|---|---|---|---|---|---|");
    foreach (var o in results)
    {
        sb.AppendLine($"| {o.Question.Id} | {o.Question.Kind} | {Cell(o.Question.Question)} | {o.Question.GoldFile} " +
                      $"| {o.DistanceRank?.ToString() ?? "-"} | {o.RerankRank?.ToString() ?? "-"} " +
                      $"| {string.Join("<br>", o.DistanceTop)} | {string.Join("<br>", o.RerankTop)} |");
    }
    sb.AppendLine();

    sb.AppendLine("## Answers side by side");
    sb.AppendLine();
    sb.AppendLine("Where the two top-" + k + " sets differ, this is the answer each one produced (same model and prompt as the app).");
    sb.AppendLine();
    foreach (var o in results.Where(o => !o.DistanceTop.SequenceEqual(o.RerankTop)))
    {
        sb.AppendLine($"### {o.Question.Id}: {o.Question.Question}");
        sb.AppendLine();
        sb.AppendLine($"Gold: `{o.Question.GoldFile}` — \"{o.Question.GoldPhrase}\"");
        sb.AppendLine();
        sb.AppendLine("**Distance order:**");
        sb.AppendLine();
        sb.AppendLine(Quote(o.DistanceAnswer));
        sb.AppendLine();
        sb.AppendLine("**Reranked:**");
        sb.AppendLine();
        sb.AppendLine(Quote(o.RerankAnswer));
        sb.AppendLine();
    }
    return sb.ToString();
}

// The same row the app uses (RagService reads the first one). The eval doesn't need rerank enabled.
static async Task<string?> ReadAppTypeSafeKeyAsync(string appConnection)
{
    await using var conn = new SqlConnection(appConnection);
    await conn.OpenAsync();
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = "IF OBJECT_ID('RerankSettings') IS NOT NULL SELECT TOP 1 ApiKey FROM RerankSettings ORDER BY Id";
    return await cmd.ExecuteScalarAsync() as string;
}

static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");
static string Quote(string s) => string.Join("\n", s.Trim().Split('\n').Select(l => "> " + l));
static string Flatten(string s) => s.Replace("\r", " ").Replace("\n", " ");

static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir != null && !File.Exists(Path.Combine(dir.FullName, "XafRag.slnx")))
        dir = dir.Parent;
    return dir?.FullName ?? throw new InvalidOperationException("XafRag.slnx not found above " + AppContext.BaseDirectory);
}

record QuestionSet(List<EvalQuestion> Questions);
record EvalQuestion(string Id, string Kind, string Question, string GoldFile, string GoldPhrase, string? Confuser);
record Outcome(
    EvalQuestion Question, int GoldInCorpus, int CandidateCount, int? DistanceRank, int? RerankRank,
    RerankScores Scores, string DistanceAnswer, string RerankAnswer, long ChatTokens,
    List<string> DistanceTop, List<string> RerankTop);
