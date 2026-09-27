using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;
using DevExpress.ExpressApp;
using DevExpress.ExpressApp.Security;
using DevExpress.Persistent.BaseImpl.EF;
using Microsoft.Data.SqlTypes;
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
    private readonly INonSecuredObjectSpaceFactory _nonSecuredObjectSpaceFactory;
    private readonly TypeSafeReranker _reranker;

    // Rerank must never make the chat slow or broken: past this budget, keep the distance order.
    public static readonly TimeSpan RerankBudget = TimeSpan.FromSeconds(5);

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
        ISecurityProvider securityProvider,
        INonSecuredObjectSpaceFactory nonSecuredObjectSpaceFactory,
        TypeSafeReranker reranker)
    {
        _ragDb = ragDb;
        _embeddingService = embeddingService;
        _chatClient = chatClient;
        _options = options.Value;
        _logger = logger;
        _objectSpaceFactory = objectSpaceFactory;
        _securityProvider = securityProvider;
        _nonSecuredObjectSpaceFactory = nonSecuredObjectSpaceFactory;
        _reranker = reranker;
    }

    // ponytail: over-fetch, then drop what the user may not read. If the user can read only a
    // small share of the nearest chunks, fewer than MaxResults survive; the upgrade is a
    // permission-aware pre-filter (push the readable parent ids into the vector query).
    private const int CandidateMultiplier = 4;

    public async Task<List<SearchResult>> SearchAsync(string query, CancellationToken ct = default)
    {
        var queryVector = await _embeddingService.GenerateEmbeddingAsync(query, ct);

        var candidates = await VectorCandidates(_ragDb, queryVector, _options).ToListAsync(ct);

        var readable = await ReadableSourcesAsync(candidates, ct);
        var permitted = new List<SearchResult>();
        foreach (var r in candidates)
        {
            if (SourceKey(r) is { } key && readable.TryGetValue(key, out var name))
            {
                r.SourceName = name;
                permitted.Add(r);
            }
        }

        // After the security filter: only chunks this user may read are ever sent to TypeSafe.
        var (ordered, rerank) = await RerankAsync(query, permitted, ct);
        var results = ordered.Take(_options.MaxResults).ToList();

        _logger.LogInformation("RAG rerank for '{Query}': {Rerank}", query, rerank);
        _logger.LogInformation("RAG search for '{Query}' returned {Count} results: {Sources}",
            query, results.Count, string.Join(" | ", results.Select(r => r.SourceName).Distinct()));
        return results;
    }

    /// <summary>
    /// The exact kNN query, nearest first, <c>MaxResults * CandidateMultiplier</c> of them. Public so
    /// the rerank evaluation (XafRag.RerankEval) measures the same ranking the app uses.
    /// </summary>
    public static IQueryable<SearchResult> VectorCandidates(RagDbContext db, SqlVector<float> queryVector, RagOptions options) =>
        db.KnowledgeChunks
            .Select(k => new SearchResult
            {
                Content = k.Content,
                Distance = EF.Functions.VectorDistance("cosine", k.Embedding!.Value, queryVector),
                ChunkIndex = k.ChunkIndex,
                SourceType = k.SourceType,
                KnowledgeArticleId = k.KnowledgeArticleId,
                DocumentId = k.DocumentId
            })
            .Where(r => r.Distance <= options.DistanceThreshold)
            .OrderBy(r => r.Distance)
            .Take(options.MaxResults * CandidateMultiplier);

    /// <summary>
    /// RAG-003 spike: re-order the permitted candidates by TypeSafe relevance when an admin has
    /// enabled it. Any failure (settings, TypeSafe, budget) keeps the distance order;
    /// only the caller's own cancellation propagates.
    /// </summary>
    private async Task<(List<SearchResult> Ordered, string Status)> RerankAsync(
        string query, List<SearchResult> permitted, CancellationToken ct)
    {
        if (permitted.Count <= _options.MaxResults)
            return (permitted, "skipped, nothing to cut");

        string? apiKey;
        try
        {
            apiKey = ReadRerankApiKey();
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Rerank settings unreadable, keeping distance order: {Error}", ex.Message);
            return (permitted, "failed: settings");
        }
        if (apiKey == null)
            return (permitted, "off");

        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(RerankBudget);
        try
        {
            var scores = await _reranker.ScoreAsync(query, permitted.Select(r => r.Content).ToList(), apiKey, budget.Token);
            // Sorted only once every passage has a score; scores are matched by input position and
            // ties keep the distance order.
            var ordered = permitted
                .Select((r, i) => (r, i))
                .OrderByDescending(x => scores.Nouls[x.i])
                .ThenBy(x => x.i)
                .Select(x => x.r)
                .ToList();
            return (ordered, $"reranked {permitted.Count} in {scores.Elapsed.TotalMilliseconds:F0} ms, " +
                             $"{scores.InputTokens} input + {scores.OutputTokens} output tokens");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning("Rerank failed, keeping distance order: {Error}", ex.Message);
            return (permitted, "failed");
        }
    }

    /// <summary>
    /// The TypeSafe key, or null when rerank is off. Read non-secured: this is server
    /// configuration, and a user without access to the settings still gets the reranked search.
    /// </summary>
    private string? ReadRerankApiKey()
    {
        using var os = _nonSecuredObjectSpaceFactory.CreateNonSecuredObjectSpace<RerankSettings>();
        var settings = os.GetObjectsQuery<RerankSettings>().OrderBy(s => s.Id).FirstOrDefault();
        if (settings is not { Enabled: true })
            return null;
        if (string.IsNullOrEmpty(settings.ApiKey))
        {
            _logger.LogWarning("Rerank is enabled but no TypeSafe API key is set; keeping distance order");
            return null;
        }
        return settings.ApiKey;
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

    /// <summary>The system message for a set of results. Public so the rerank evaluation prompts identically.</summary>
    public static string BuildSystemMessage(IReadOnlyCollection<SearchResult> searchResults)
    {
        var contextText = searchResults.Count > 0
            ? string.Join("\n\n---\n\n", searchResults.Select(r =>
                $"**[Part {r.ChunkIndex + 1} of \"{r.SourceName}\"]** {r.Content}"))
            : "No relevant context found in the knowledge base.";
        return $"{SystemPrompt}\n\n## Context:\n{contextText}";
    }

    public async IAsyncEnumerable<string> AskAsync(
        string question,
        IList<ChatMessage>? conversationHistory = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        var searchResults = await SearchAsync(question, ct);

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, BuildSystemMessage(searchResults))
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
