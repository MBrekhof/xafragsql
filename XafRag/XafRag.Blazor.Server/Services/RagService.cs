using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.BaseImpl.EF;
using XafRag.Blazor.Server.Configuration;
using XafRag.Module.BusinessObjects;

namespace XafRag.Blazor.Server.Services;

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
    private readonly IObjectSpaceFactory _objectSpaceFactory;
    private readonly ISecurityProvider _securityProvider;

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
        ILogger<RagService> logger,
        IObjectSpaceFactory objectSpaceFactory,
        ISecurityProvider securityProvider)
    {
        _ragDb = ragDb;
        _embeddingService = embeddingService;
        _chatClient = chatClient;
        _options = options.Value;
        _logger = logger;
        _objectSpaceFactory = objectSpaceFactory;
        _securityProvider = securityProvider;
    }

    // ponytail: over-fetch, then drop what the user may not read. If the user can read only a
    // small share of the nearest chunks, fewer than MaxResults survive; the upgrade is a
    // permission-aware pre-filter (push the readable parent ids into the vector query).
    private const int CandidateMultiplier = 4;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var queryVector = await _embeddingService.GenerateEmbeddingAsync(query, ct);

        var candidates = await _ragDb.KnowledgeChunks
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
            .Take(_options.MaxResults * CandidateMultiplier)
            .ToListAsync(ct);

        var readable = await ReadableSourcesAsync(candidates, ct);
        var results = new List<SearchResult>();
        foreach (var r in candidates)
        {
            if (SourceKey(r) is { } key && readable.TryGetValue(key, out var name))
            {
                r.SourceName = name;
                results.Add(r);
                if (results.Count == _options.MaxResults) break;
            }
        }

        _logger.LogInformation("RAG search for '{Query}' returned {Count} results: {Sources}",
            query, results.Count, string.Join(" | ", results.Select(r => r.SourceName).Distinct()));
        return results;
    }

    private static (ChunkSourceType, int)? SourceKey(SearchResult r) => r.SourceType switch
    {
        ChunkSourceType.Article when r.KnowledgeArticleId is { } id => (ChunkSourceType.Article, id),
        ChunkSourceType.Document when r.DocumentId is { } id => (ChunkSourceType.Document, id),
        _ => null // no parent: nothing to check permissions against, so it never reaches the prompt
    };

    /// <summary>
    /// The candidates' parents the current user may read, with their display names. Chunks live in
    /// RagDbContext, outside XAF security, so this is where the security system gets its say: the
    /// parents are loaded through a secured Object Space (type and object permissions), then the
    /// member the chunk text came from is checked (member permissions).
    /// </summary>
    private async Task<Dictionary<(ChunkSourceType, int), string>> ReadableSourcesAsync(
        List<SearchResult> candidates, CancellationToken ct)
    {
        var readable = new Dictionary<(ChunkSourceType, int), string>();
        var keys = candidates.Select(SourceKey).OfType<(ChunkSourceType Type, int Id)>().Distinct().ToList();
        if (keys.Count == 0) return readable;

        var security = (IRequestSecurityStrategy)_securityProvider.GetSecurity();
        using var os = _objectSpaceFactory.CreateObjectSpace<KnowledgeArticle>();

        var articleIds = keys.Where(k => k.Type == ChunkSourceType.Article).Select(k => k.Id).ToList();
        if (articleIds.Count > 0)
        {
            var articles = await os.GetObjectsQuery<KnowledgeArticle>()
                .Where(a => articleIds.Contains(a.Id)).ToListAsync(ct);
            foreach (var a in articles)
            {
                if (security.CanRead(typeof(KnowledgeArticle), os, a.Id, nameof(KnowledgeArticle.Content)))
                    readable[(ChunkSourceType.Article, a.Id)] =
                        NameOrRestricted(security, os, typeof(KnowledgeArticle), a.Id, nameof(KnowledgeArticle.Title), a.Title);
            }
        }

        var documentIds = keys.Where(k => k.Type == ChunkSourceType.Document).Select(k => k.Id).ToList();
        if (documentIds.Count > 0)
        {
            var documents = await os.GetObjectsQuery<Document>()
                .Where(d => documentIds.Contains(d.Id)).ToListAsync(ct);
            foreach (var d in documents)
            {
                // The chunk text is the uploaded file: Document.FileData, then that object's Content.
                if (d.FileData == null
                    || !security.CanRead(typeof(Document), os, d.Id, nameof(Document.FileData))
                    || !security.CanRead(typeof(FileData), os, os.GetKeyValue(d.FileData), nameof(FileData.Content)))
                    continue;
                readable[(ChunkSourceType.Document, d.Id)] =
                    NameOrRestricted(security, os, typeof(Document), d.Id, nameof(Document.FileName), d.FileName);
            }
        }

        return readable;
    }

    private static string NameOrRestricted(IRequestSecurityStrategy security, IObjectSpace os,
        Type type, object key, string member, string? value) =>
        security.CanRead(type, os, key, member) && !string.IsNullOrEmpty(value) ? value : "restricted source";

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
