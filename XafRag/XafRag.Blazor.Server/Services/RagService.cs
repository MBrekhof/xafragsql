using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlTypes;
using XafRag.Blazor.Server.Configuration;
using XafRag.Module.BusinessObjects;

namespace XafRag.Blazor.Server.Services;

internal class SourceNameRow
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
}

public class SearchResult
{
    public string Content { get; set; } = string.Empty;
    public double Distance { get; set; }
    public int ChunkIndex { get; set; }
    public ChunkSourceType SourceType { get; set; }
    public int? KnowledgeArticleId { get; set; }
    public int? DocumentId { get; set; }
    public string SourceName { get; set; } = string.Empty;
}

public class RagService
{
    private readonly RagDbContext _ragDb;
    private readonly EmbeddingService _embeddingService;
    private readonly IChatClient _chatClient;
    private readonly RagOptions _options;
    private readonly ILogger<RagService> _logger;

    private const string SystemPrompt = """
        You are a helpful knowledge base assistant. Use the provided context to answer the user's question.
        If the context does not contain enough information to answer, say so honestly.
        Always base your answers on the provided context. Do not make up information.
        When possible, mention which source the information came from.
        Format your responses using Markdown for readability.
        """;

    public RagService(
        RagDbContext ragDb,
        EmbeddingService embeddingService,
        IChatClient chatClient,
        IOptions<RagOptions> options,
        ILogger<RagService> logger)
    {
        _ragDb = ragDb;
        _embeddingService = embeddingService;
        _chatClient = chatClient;
        _options = options.Value;
        _logger = logger;
    }

    // SQL Server has no array parameter equivalent to Postgres ANY(), so each id is bound as its
    // own parameter. The SQL is assembled as a plain string rather than an interpolated literal,
    // which keeps the values parameterised instead of concatenated in.
    private static (string Sql, object[] Parameters) InClause(string selectUpToColumn, List<int> ids)
    {
        var names = ids.Select((_, i) => "@id" + i).ToArray();
        var sql = selectUpToColumn + "IN (" + string.Join(",", names) + ")";
        var parameters = ids.Select((id, i) => (object)new SqlParameter(names[i], id)).ToArray();
        return (sql, parameters);
    }

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var queryVector = await _embeddingService.GenerateEmbeddingAsync(query, ct);

        var results = await _ragDb.KnowledgeChunks
            .Select(k => new SearchResult
            {
                Content = k.Content,
                Distance = EF.Functions.VectorDistance("cosine", k.Embedding!.Value, queryVector),
                ChunkIndex = k.ChunkIndex,
                SourceType = k.SourceType,
                KnowledgeArticleId = k.KnowledgeArticleId,
                DocumentId = k.DocumentId
            })
            .Where(r => r.Distance <= _options.DistanceThreshold)
            .OrderBy(r => r.Distance)
            .Take(_options.MaxResults)
            .ToListAsync(ct);

        // Resolve source names
        var docIds = results.Where(r => r.SourceType == ChunkSourceType.Document && r.DocumentId.HasValue)
            .Select(r => r.DocumentId!.Value).Distinct().ToList();
        var articleIds = results.Where(r => r.SourceType == ChunkSourceType.Article && r.KnowledgeArticleId.HasValue)
            .Select(r => r.KnowledgeArticleId!.Value).Distinct().ToList();

        var docNames = new Dictionary<int, string>();
        if (docIds.Count > 0)
        {
            var (sql, ps) = InClause("""SELECT "Id", "FileName" AS "Name" FROM "Documents" WHERE "Id" """, docIds);
            docNames = await _ragDb.Database.SqlQueryRaw<SourceNameRow>(sql, ps)
                .ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        }

        var articleNames = new Dictionary<int, string>();
        if (articleIds.Count > 0)
        {
            var (sql, ps) = InClause("""SELECT "Id", "Title" AS "Name" FROM "KnowledgeArticles" WHERE "Id" """, articleIds);
            articleNames = await _ragDb.Database.SqlQueryRaw<SourceNameRow>(sql, ps)
                .ToDictionaryAsync(r => r.Id, r => r.Name, ct);
        }

        foreach (var r in results)
        {
            r.SourceName = r.SourceType switch
            {
                ChunkSourceType.Document when r.DocumentId.HasValue && docNames.TryGetValue(r.DocumentId.Value, out var name) => name,
                ChunkSourceType.Article when r.KnowledgeArticleId.HasValue && articleNames.TryGetValue(r.KnowledgeArticleId.Value, out var name) => name,
                _ => $"{r.SourceType} #{r.DocumentId ?? r.KnowledgeArticleId}"
            };
        }

        _logger.LogInformation("RAG search for '{Query}' returned {Count} results", query, results.Count);
        return results;
    }

    public async IAsyncEnumerable<string> AskAsync(
        string question,
        IList<ChatMessage>? conversationHistory = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var searchResults = await SearchAsync(question, ct);

        var contextText = searchResults.Count > 0
            ? string.Join("\n\n---\n\n", searchResults.Select(r =>
                $"**[Part {r.ChunkIndex + 1} of \"{r.SourceName}\"]** {r.Content}"))
            : "No relevant context found in the knowledge base.";

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, $"{SystemPrompt}\n\n## Context:\n{contextText}")
        };

        if (conversationHistory != null)
        {
            messages.AddRange(conversationHistory);
        }

        messages.Add(new(ChatRole.User, question));

        await foreach (var update in _chatClient.GetStreamingResponseAsync(messages, cancellationToken: ct))
        {
            if (update.Text is { } text)
            {
                yield return text;
            }
        }
    }
}
