# How to Add RAG to Your Own XAF Application

This guide walks you through adding Retrieval-Augmented Generation (RAG) to an existing DevExpress XAF Blazor Server application. It covers every layer — from database setup to chat UI — using the patterns from the XafRagSQL sample, which stores embeddings in SQL Server 2025's native `VECTOR` type.

For the complete source code of every file mentioned here, refer to this repository.

---

## Prerequisites

- .NET 10 SDK with a working XAF Blazor Server project (DevExpress 26.1.4 or later in the 26.1 line)
- Docker (for SQL Server 2025)
- **SQL Server 2025 (v17) or later.** The `VECTOR` type does not exist in SQL Server 2022 or earlier and there is no downgrade path. Check with `SELECT @@VERSION` before you start.
- An OpenAI API key
- DevExpress NuGet feed configured

---

## Step 1: Add Docker for SQL Server 2025

Skip this step if you already have a SQL Server 2025 instance. Otherwise create (or extend)
`docker-compose.yml` at your repo root:

```yaml
services:
  mssql:
    image: mcr.microsoft.com/mssql/server:2025-latest
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "Your_Strong_Pw_2025!"
      MSSQL_PID: Developer
    ports:
      - "14333:1433"
    volumes:
      - mssql-data:/var/opt/mssql

volumes:
  mssql-data:
```

```bash
docker compose up -d
```

Confirm you actually got v17 — the `VECTOR` type is silently absent on anything older:

```bash
docker exec <container> /opt/mssql-tools18/bin/sqlcmd   -S localhost -U sa -P 'Your_Strong_Pw_2025!' -C -Q "SELECT @@VERSION"
```

---

## Step 2: Install NuGet Packages

No vector-specific package is needed. EF Core 10's SQL Server provider has `VECTOR` support
built in, and it pulls `Microsoft.Data.SqlClient` 6.1+ (which supplies `SqlVector<T>`)
transitively — you do not need to reference it explicitly.

> If you are coming from the `EFCore.SqlServer.VectorSearch` extension, remove it. EF Core 10's
> built-in support replaces it.

### Module project (.Module.csproj)

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.12" />
```

### Blazor Server project (.Blazor.Server.csproj)

```xml
<PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="10.0.12" />
<PackageReference Include="Microsoft.Extensions.AI" Version="10.8.0" />
<PackageReference Include="Microsoft.Extensions.AI.OpenAI" Version="10.8.0" />
<PackageReference Include="DevExpress.AIIntegration.Blazor.Chat" Version="26.1.4" />
<PackageReference Include="DevExpress.Document.Processor" Version="26.1.4" />
<PackageReference Include="Markdig" Version="1.3.2" />
<PackageReference Include="HtmlSanitizer" Version="9.0.892" />
<PackageReference Include="Serilog.AspNetCore" Version="10.0.0" />
<PackageReference Include="Serilog.Sinks.File" Version="7.0.0" />
```

---

## Step 3: Configuration

### appsettings.json

Add the OpenAI and RAG sections (keep `ApiKey` empty here):

```json
"OpenAI": {
  "ApiKey": "",
  "EmbeddingModel": "text-embedding-3-small",
  "ChatModel": "gpt-4o"
},
"Rag": {
  "ChunkTokenLimit": 500,
  "ChunkOverlap": 100,
  "MaxResults": 5,
  "DistanceThreshold": 1.0
}
```

### appsettings.Development.json (gitignored)

Place your actual API key here:

```json
{
  "OpenAI": {
    "ApiKey": "sk-your-key-here"
  }
}
```

Make sure `appsettings.Development.json` is in your `.gitignore`.

### Options classes

```csharp
// Configuration/OpenAiOptions.cs
public class OpenAiOptions
{
    public const string SectionName = "OpenAI";
    public string ApiKey { get; set; } = string.Empty;
    public string EmbeddingModel { get; set; } = "text-embedding-3-small";
    public string ChatModel { get; set; } = "gpt-4o";
}

// Configuration/RagOptions.cs
public class RagOptions
{
    public const string SectionName = "Rag";
    public int ChunkTokenLimit { get; set; } = 500;
    public int ChunkOverlap { get; set; } = 100;
    public int MaxResults { get; set; } = 5;
    public double DistanceThreshold { get; set; } = 1.0;
}
```

---

## Step 4: Data Model

### XAF entities (Module project)

**KnowledgeArticle** — a standard XAF entity for manually authored content:

```csharp
[DefaultClassOptions]
[NavigationItem("Knowledge Base")]
[DefaultProperty(nameof(Title))]
public class KnowledgeArticle : IXafEntityObject
{
    [Key]
    public virtual int Id { get; set; }

    [FieldSize(200)]
    public virtual string Title { get; set; } = string.Empty;

    [FieldSize(FieldSizeAttribute.Unlimited)]
    public virtual string Content { get; set; } = string.Empty;

    public virtual DateTime CreatedDate { get; set; }
    public virtual DateTime ModifiedDate { get; set; }

    public void OnCreated() { CreatedDate = ModifiedDate = DateTime.UtcNow; }
    public void OnSaving()  { ModifiedDate = DateTime.UtcNow; }
    public void OnLoaded()  { }
}
```

**Document** — uses XAF's `FileData` for binary uploads. The filename is auto-populated from the uploaded file:

```csharp
[DefaultClassOptions]
[NavigationItem("Knowledge Base")]
[DefaultProperty(nameof(FileName))]
public class Document : IXafEntityObject
{
    [Key]
    public virtual int Id { get; set; }

    [FieldSize(500)]
    public virtual string FileName { get; set; } = string.Empty;

    public virtual FileData? FileData { get; set; }
    public virtual DocumentStatus Status { get; set; }
    public virtual DateTime CreatedDate { get; set; }

    public void OnCreated()
    {
        CreatedDate = DateTime.UtcNow;
        Status = DocumentStatus.Pending;
    }

    public void OnSaving()
    {
        if (FileData != null && !string.IsNullOrEmpty(FileData.FileName))
            FileName = FileData.FileName;
    }

    public void OnLoaded() { }
}

public enum DocumentStatus { Pending, Processing, Completed, Failed }
```

**RagChatHolder** — a non-persistent placeholder. XAF needs a business object to open a DetailView, even when there is no database record:

```csharp
[DomainComponent]
[DefaultClassOptions]
[NavigationItem("Knowledge Base")]
[DisplayName("RAG Chat")]
public class RagChatHolder : NonPersistentBaseObject { }
```

Register `KnowledgeArticle` and `Document` as `DbSet` properties in your `XafEFCoreDbContext`. Do **not** register `RagChatHolder` — it is non-persistent.

### KnowledgeChunk and RagDbContext (Module project)

`KnowledgeChunk` stores text chunks and their embeddings. It uses standard EF Core annotations — XAF must not manage this entity:

```csharp
[Table("knowledge_chunks")]
public class KnowledgeChunk
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Column("content")]
    public string Content { get; set; } = string.Empty;

    [Column("embedding", TypeName = "vector(1536)")]
    public SqlVector<float>? Embedding { get; set; }

    [Column("token_count")]
    public int TokenCount { get; set; }

    [Column("chunk_index")]
    public int ChunkIndex { get; set; }

    [Column("source_type")]
    public ChunkSourceType SourceType { get; set; }

    [Column("knowledge_article_id")]
    public int? KnowledgeArticleId { get; set; }

    [Column("document_id")]
    public int? DocumentId { get; set; }
}

public enum ChunkSourceType { Article, Document }
```

`SqlVector<float>` lives in `Microsoft.Data.SqlTypes`.

The dimension `vector(1536)` matches `text-embedding-3-small`. **SQL Server caps a float32 vector
column at 1998 dimensions**, so `text-embedding-3-large` (3072) does not fit — you would need to
request reduced dimensions from the embedding API, or use the half-precision float16 vector type,
which allows up to 3996.

`RagDbContext` is a standalone `DbContext` completely separate from XAF's context:

```csharp
public class RagDbContext : DbContext
{
    public RagDbContext(DbContextOptions<RagDbContext> options) : base(options) { }
    public DbSet<KnowledgeChunk> KnowledgeChunks { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        modelBuilder.Entity<KnowledgeChunk>(e =>
        {
            e.HasIndex(x => x.KnowledgeArticleId);
            e.HasIndex(x => x.DocumentId);
        });
    }
}
```

**Why a separate DbContext?** XAF's EF Core context uses change-tracking proxies, deferred
deletion and optimistic locking, none of which you want wrapped around a large binary embedding
column. A dedicated `RagDbContext` keeps the RAG storage fully owned by your own code — and,
usefully, means `SqlVector<float>` never meets XAF's proxy generation.

---

## Step 5: Services

All services go in the Blazor Server project under `Services/`.

### ChunkingService

Splits text into overlapping chunks at paragraph boundaries. Paragraphs exceeding the token limit are split further at sentence boundaries. Token count is estimated as `text.Length / 4`. Default: 500 tokens per chunk, 100-token overlap.

### EmbeddingService

Wraps `IEmbeddingGenerator<string, Embedding<float>>` from `Microsoft.Extensions.AI`:

```csharp
public async Task<SqlVector<float>> GenerateEmbeddingAsync(string text, CancellationToken ct = default)
{
    var result = await _embeddingGenerator.GenerateAsync([text], cancellationToken: ct);
    return new SqlVector<float>(result[0].Vector);
}
```

### DocumentProcessingService

Extracts plain text from uploaded files using DevExpress document processors:

```csharp
return extension switch
{
    ".txt" or ".md" => Encoding.UTF8.GetString(fileBytes),
    ".pdf"  => ExtractFromPdf(fileBytes),    // PdfDocumentProcessor.GetText()
    ".docx" => ExtractFromDocx(fileBytes),   // RichEditDocumentServer + OpenXml
    _ => throw new NotSupportedException($"Unsupported file type: {extension}")
};
```

### RagService

Combines vector search with LLM chat. Key features:

- **Source name resolution**: after the vector search, the service queries the `Documents` and `KnowledgeArticles` tables to resolve actual filenames/titles. Those tables belong to XAF's `DbContext`, not `RagDbContext`, so there is no query root to join against — the lookup is raw SQL. Bind each id as its own parameter (`WHERE [Id] IN (@id0, @id1, ...)`); SQL Server has no array parameter equivalent to Postgres `ANY()`
- **Context formatting**: chunks are labeled as `**[Part N of "filename.md"]**` so the LLM can cite sources with bold references
- **Streaming**: `AskAsync` returns `IAsyncEnumerable<string>` for token-by-token rendering

The search itself projects the distance into a DTO, then filters and orders on it:

```csharp
var results = await _ragDb.KnowledgeChunks
    .Select(k => new SearchResult
    {
        Content  = k.Content,
        Distance = EF.Functions.VectorDistance("cosine", k.Embedding!.Value, queryVector),
        ChunkIndex = k.ChunkIndex,
        // ...
    })
    .Where(r => r.Distance <= _options.DistanceThreshold)
    .OrderBy(r => r.Distance)
    .Take(_options.MaxResults)
    .ToListAsync(ct);
```

EF flattens that projection, so it becomes one statement with no subquery — the distance
expression is repeated into `WHERE` and `ORDER BY`:

```sql
SELECT TOP(@p) [k].[content] AS [Content],
       VECTOR_DISTANCE('cosine', [k].[embedding], @queryVector) AS [Distance], ...
FROM [knowledge_chunks] AS [k]
WHERE VECTOR_DISTANCE('cosine', [k].[embedding], @queryVector) <= @_options_DistanceThreshold
ORDER BY VECTOR_DISTANCE('cosine', [k].[embedding], @queryVector)
```

This is an **exact kNN scan over every row**. That is a deliberate choice: SQL Server 2025's
`VECTOR_SEARCH()` and vector indexes are experimental and their EF Core APIs are documented as
subject to change. When row counts justify it, add `HasVectorIndex()` to the model and switch to
`VectorSearch(...).OrderBy(r => r.Distance).Take(n).WithApproximate()`.

```csharp
var contextText = searchResults.Count > 0
    ? string.Join("\n\n---\n\n", searchResults.Select(r =>
        $"**[Part {r.ChunkIndex + 1} of \"{r.SourceName}\"]** {r.Content}"))
    : "No relevant context found in the knowledge base.";
```

### IngestionService

Runs ingestion on a background thread via `Task.Run`. Each invocation creates its own DI scope. After completion, it updates the Document status to `Completed` or `Failed` via direct SQL:

```csharp
private static async Task UpdateDocumentStatus(RagDbContext ragDb, int documentId, DocumentStatus status)
{
    await ragDb.Database.ExecuteSqlRawAsync(
        """UPDATE "Documents" SET "Status" = {0} WHERE "Id" = {1}""",
        (int)status, documentId);
}
```

---

## Step 6: Ingestion Controllers

### DocumentIngestionController

Key patterns:
- **Re-entrancy guard**: prevents infinite loop when `CommitChanges()` inside `Committed` fires `Committed` again
- **Status check**: only processes documents in `Pending` status
- **Filename fallback**: uses `FileData.FileName` if `Document.FileName` is empty

```csharp
public class DocumentIngestionController : ObjectViewController<DetailView, Document>
{
    private bool _isCommitting;

    private void ObjectSpace_Committed(object? sender, EventArgs e)
    {
        if (_isCommitting) return;

        var doc = ViewCurrentObject;
        if (doc?.FileData == null || doc.Status != DocumentStatus.Pending) return;

        using var ms = new MemoryStream();
        doc.FileData.SaveToStream(ms);
        var bytes = ms.ToArray();
        if (bytes.Length == 0) return;

        var fileName = !string.IsNullOrEmpty(doc.FileName)
            ? doc.FileName : doc.FileData.FileName;

        _isCommitting = true;
        try
        {
            doc.Status = DocumentStatus.Processing;
            ObjectSpace.CommitChanges();
        }
        finally { _isCommitting = false; }

        _ingestionService?.IngestDocumentInBackground(doc.Id, fileName, bytes);
    }
}
```

Create a matching `KnowledgeArticleIngestionController` for articles — it is simpler since there is no file extraction or status tracking.

---

## Step 7: Chat UI

### RagChatComponent.razor

The Razor component wraps `DxAIChat` with manual message handling (bypassing the built-in AI client to inject RAG context):

```razor
@inject RagService RagService

<DxAIChat CssClass="rag-chat"
          ShowHeader="true"
          HeaderText="Knowledge Base Assistant"
          UseStreaming="false"
          ResponseContentFormat="ResponseContentFormat.Markdown"
          MessageSending="OnMessageSending">
    <MessageContentTemplate>
        <div class="rag-chat-content">
            @ToHtml(context.Text)
        </div>
    </MessageContentTemplate>
    <EmptyMessageAreaTemplate>
        <div class="rag-chat-empty">
            <h3>Knowledge Base Assistant</h3>
            <p>Ask questions about your knowledge base.</p>
        </div>
    </EmptyMessageAreaTemplate>
</DxAIChat>

@code {
    private readonly HtmlSanitizer _sanitizer = new();

    private async Task OnMessageSending(MessageSendingEventArgs args)
    {
        // Cancel built-in delivery: the RAG pipeline replaces the component's AI service.
        args.Cancel = true;
        await args.Chat.AppendMessageAsync(args.Text, ChatRole.User);
        await args.Chat.ShowLoadingIndicatorAsync("Searching knowledge base...");
        try
        {
            var sb = new System.Text.StringBuilder();
            await foreach (var chunk in RagService.AskAsync(args.Text, ct: args.CancellationToken))
                sb.Append(chunk);
            await args.Chat.AppendMessageAsync(sb.ToString(), ChatRole.Assistant);
        }
        finally
        {
            await args.Chat.HideLoadingIndicatorAsync();
        }
    }

    private MarkupString ToHtml(string markdown)
    {
        if (string.IsNullOrEmpty(markdown)) return new MarkupString(string.Empty);
        var html = Markdown.ToHtml(markdown);
        return new MarkupString(_sanitizer.Sanitize(html));
    }
}
```

`ShowHeader="true"` enables the built-in **Clear Chat** button.

### RagChatViewItem

XAF Blazor renders custom UI through `IComponentContentHolder`:

```csharp
public interface IModelRagChatViewItem : IModelViewItem { }

[ViewItem(typeof(IModelRagChatViewItem))]
public class RagChatViewItem(IModelViewItem model, Type objectType)
    : ViewItem(objectType, model.Id),
      IComponentContentHolder, IComplexViewItem
{
    private RagChatComponentModel? _componentModel;

    RenderFragment IComponentContentHolder.ComponentContent =>
        ComponentModelObserver.Create(_componentModel!, _componentModel!.GetComponentContent());

    protected override object CreateControlCore()
    {
        _componentModel = new RagChatComponentModel();
        return _componentModel;
    }

    void IComplexViewItem.Setup(IObjectSpace os, XafApplication app) { }
}

public class RagChatComponentModel : ComponentModelBase
{
    public override Type ComponentType => typeof(RagChatComponent);
}
```

### RagChatDetailViewUpdater

Programmatically adds the ViewItem to the DetailView layout so you don't need to use the Model Editor:

```csharp
public class RagChatDetailViewUpdater : ModelNodesGeneratorUpdater<ModelViewsNodesGenerator>
{
    public override void UpdateNode(ModelNode node)
    {
        var views = (IModelViews)node;
        if (views["RagChatHolder_DetailView"] is not IModelDetailView dv) return;

        const string chatItemId = "RagChatItem";
        if (dv.Items[chatItemId] == null)
            dv.Items.AddNode<IModelRagChatViewItem>(chatItemId);

        // Remove the default Oid property editor
        var oidItem = dv.Items["Oid"];
        if (oidItem != null) ((IModelNode)oidItem).Remove();

        // Rebuild layout with only the chat item
        var layout = dv.Layout;
        if (layout == null) return;
        for (int i = layout.Count - 1; i >= 0; i--)
            layout[i].Remove();

        var chatLayoutItem = layout.AddNode<IModelLayoutViewItem>(chatItemId);
        chatLayoutItem.ViewItem = (IModelViewItem)dv.Items[chatItemId];
    }
}
```

Register it in your Blazor module:

```csharp
public override void AddGeneratorUpdaters(ModelNodesGeneratorUpdaters updaters)
{
    base.AddGeneratorUpdaters(updaters);
    updaters.Add(new RagChatDetailViewUpdater());
}
```

### RagChatWindowController

Since `RagChatHolder` is non-persistent, XAF generates a ListView by default. This controller intercepts navigation and redirects to the DetailView:

```csharp
public class RagChatWindowController : WindowController
{
    public RagChatWindowController() { TargetWindowType = WindowType.Main; }

    protected override void OnActivated()
    {
        base.OnActivated();
        var navController = Frame.GetController<ShowNavigationItemController>();
        if (navController != null)
            navController.CustomShowNavigationItem += OnCustomShowNavigationItem;
    }

    protected override void OnDeactivated()
    {
        var navController = Frame.GetController<ShowNavigationItemController>();
        if (navController != null)
            navController.CustomShowNavigationItem -= OnCustomShowNavigationItem;
        base.OnDeactivated();
    }

    private void OnCustomShowNavigationItem(object? sender, CustomShowNavigationItemEventArgs e)
    {
        if (e.ActionArguments.SelectedChoiceActionItem?.Data is ViewShortcut shortcut
            && shortcut.ViewId == "RagChatHolder_ListView")
        {
            var objectSpace = Application.CreateObjectSpace(typeof(RagChatHolder));
            var holder = objectSpace.CreateObject<RagChatHolder>();
            var detailView = Application.CreateDetailView(objectSpace, holder);
            detailView.ViewEditMode = ViewEditMode.View;
            e.ActionArguments.ShowViewParameters.CreatedView = detailView;
            e.Handled = true;
        }
    }
}
```

**Important:** Do not use `Frame.SetView()` — it disposes the ListView while Blazor is still rendering it. Always use `ShowViewParameters.CreatedView` via `CustomShowNavigationItem`.

---

## Step 8: Register Everything in Startup.cs

Add before `AddXaf`:

```csharp
// 1. RagDbContext on SQL Server - no vector-specific wiring needed
var ragConnStr = Configuration.GetConnectionString("ConnectionString");
#if EASYTEST
if (Configuration.GetConnectionString("EasyTestConnectionString") != null)
{
    ragConnStr = Configuration.GetConnectionString("EasyTestConnectionString");
}
#endif
ArgumentNullException.ThrowIfNull(ragConnStr);

services.AddDbContext<RagDbContext>((sp, options) =>
    options.UseSqlServer(ragConnStr));

// 2. Configuration
services.Configure<OpenAiOptions>(Configuration.GetSection("OpenAI"));
services.Configure<RagOptions>(Configuration.GetSection("Rag"));

// 3. OpenAI clients
var openAiOptions = Configuration.GetSection(OpenAiOptions.SectionName).Get<OpenAiOptions>()!;
var openAiClient = new OpenAIClient(openAiOptions.ApiKey);

IChatClient chatClient = openAiClient
    .GetChatClient(openAiOptions.ChatModel).AsIChatClient();
services.AddChatClient(chatClient);

IEmbeddingGenerator<string, Embedding<float>> embeddingGenerator = openAiClient
    .GetEmbeddingClient(openAiOptions.EmbeddingModel).AsIEmbeddingGenerator();
services.AddSingleton(embeddingGenerator);

// 4. DevExpress AI
services.AddDevExpressAI();

// 5. RAG services
services.AddScoped<ChunkingService>();
services.AddScoped<EmbeddingService>();
services.AddScoped<DocumentProcessingService>();
services.AddScoped<RagService>();
services.AddSingleton<IngestionService>();
```

After `UseXaf()` in `Configure`, create the vector table:

```csharp
using (var scope = app.ApplicationServices.CreateScope())
{
    var ragDb = scope.ServiceProvider.GetRequiredService<RagDbContext>();
    ragDb.Database.Migrate();
    ragDb.Database.ExecuteSqlRaw(/* cascade FK script, see below */);
}
```

**Use `Migrate()`, not `EnsureCreated()` — this one will bite you.** `EnsureCreated()` does nothing
if the database already contains tables. XAF creates its own schema first in several common flows
(most notably `YourApp.exe --updateDatabase`, which returns before the web host ever starts, so
this block never runs at all). On the next normal start `EnsureCreated()` sees a populated
database, returns `false`, and your chunks table is never created — RAG then fails silently
forever. Give `RagDbContext` a real migration instead:

```bash
dotnet ef migrations add InitialRagSchema --context RagDbContext
```

Add an `IDesignTimeDbContextFactory<RagDbContext>` next to the context so the EF tools can build
it without booting the whole XAF host (which would demand an API key and a live database).

Because the chunks table and the XAF entities live in different `DbContext`s, EF cannot declare a
relationship between them — so the reference implementation adds real foreign keys with
`ON DELETE CASCADE` at the database level, guarded so it is idempotent:

```sql
IF OBJECT_ID('[Documents]', 'U') IS NOT NULL
   AND OBJECT_ID('[knowledge_chunks]', 'U') IS NOT NULL
   AND NOT EXISTS (SELECT 1 FROM sys.foreign_keys WHERE name = 'fk_knowledge_chunks_document')
BEGIN
    DELETE kc FROM knowledge_chunks kc
      WHERE kc.document_id IS NOT NULL
        AND NOT EXISTS (SELECT 1 FROM [Documents] d WHERE d.[Id] = kc.document_id);
    ALTER TABLE knowledge_chunks ADD CONSTRAINT fk_knowledge_chunks_document
      FOREIGN KEY (document_id) REFERENCES [Documents]([Id]) ON DELETE CASCADE;
END;
```

**Keep this out of the migration.** XAF creates `Documents` and `KnowledgeArticles` *after* the RAG
migration runs, so a migration would either fail on a missing target or — if written defensively —
skip silently and then never re-run, because EF marks it applied. As a guarded startup step it
self-heals instead: on a brand-new database the FKs are simply added on the next start. Existing
orphans are deleted first, or the constraint would fail.

Deleting a `Document` or `KnowledgeArticle` then removes its chunks automatically, with no
orphan-cleanup code. This works because XAF hard-deletes these entities: they do not implement
`IDeferredDeletion`, so XAF's soft-deletion (`GCRecord`) does not apply to them.

### Serilog setup (Program.cs)

```csharp
.UseSerilog((context, configuration) =>
{
    configuration
        .ReadFrom.Configuration(context.Configuration)
        .WriteTo.Console()
        .WriteTo.File("logs/xafrag-.log",
            rollingInterval: RollingInterval.Day,
            outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");
})
```

---

## Step 9: Security Permissions

In your `Updater.cs`, grant the default role access to the new entities:

```csharp
defaultRole.AddTypePermissionsRecursively<KnowledgeArticle>(
    SecurityOperations.CRUDAccess, SecurityPermissionState.Allow);
defaultRole.AddTypePermissionsRecursively<Document>(
    SecurityOperations.CRUDAccess, SecurityPermissionState.Allow);
```

---

## Key Decisions and Gotchas

### Why a separate RagDbContext?

XAF's EF Core context uses change-tracking proxies, deferred deletion and optimistic locking.
Mixing a large binary embedding column into that pipeline is fragile, and change-tracking proxies
require `virtual` members that `SqlVector<float>` has no business carrying. A dedicated
`RagDbContext` is simpler and fully owned by your RAG code.

### Document status tracking

The `IngestionService` runs on a background thread without access to XAF's `ObjectSpace`. It updates the `Document.Status` column via direct SQL through `RagDbContext.Database.ExecuteSqlRawAsync`. This avoids creating an `ObjectSpace` from a background thread (which requires careful security context handling).

### Re-entrancy in ObjectSpace.Committed

When the `DocumentIngestionController` sets `Status = Processing` and calls `ObjectSpace.CommitChanges()` inside the `Committed` handler, it fires `Committed` again. Use a boolean `_isCommitting` guard to prevent infinite recursion.

### ListView → DetailView redirect for non-persistent objects

XAF generates a ListView for `RagChatHolder` by default. Do not use `Frame.SetView()` to redirect — it disposes the ListView while Blazor is still rendering, causing an `ObjectDisposedException`. Instead, use `CustomShowNavigationItem` and set `ShowViewParameters.CreatedView`.

### `EnsureCreated()` silently skips the chunks table

Covered in Step 8, and worth repeating: use `Migrate()`. With `EnsureCreated()`, any flow where
XAF builds the schema first leaves `knowledge_chunks` permanently absent. The failure is quiet at
*creation* time - nothing complains during startup - but loud at *use* time: the first search
throws a database exception for the missing object. `SearchAsync` does not catch it, so it
surfaces as an error in the chat, not as an empty result. If you see "no relevant context found"
instead, your table exists and the problem is elsewhere (embeddings never written, or a distance
threshold that excludes everything).

### Adopting a database that already has `knowledge_chunks`

If you previously created the table with `EnsureCreated()` and are switching to migrations, the
database has the table but no `__EFMigrationsHistory` row for it. `Migrate()` then considers the
initial migration unapplied and tries to `CREATE TABLE` something that already exists, and startup
fails. Baseline it instead: insert the migration's id into `__EFMigrationsHistory` so EF treats it
as already applied. Microsoft documents that moving from `EnsureCreated()` to migrations is not
seamless.

### `sys.columns` reports the vector column as `varbinary`

`TYPE_NAME(system_type_id)` returns the underlying storage type, so a correctly created vector
column looks like `varbinary` at first glance. Join `sys.types` on `user_type_id` instead — a real
vector column reports `type = vector` with `vector_dimensions` and `vector_base_type_desc`
populated:

```sql
SELECT c.name, t.name AS type_name, c.vector_dimensions, c.vector_base_type_desc
FROM sys.columns c JOIN sys.types t ON c.user_type_id = t.user_type_id
WHERE c.object_id = OBJECT_ID('knowledge_chunks');
```

### Vector column limitations

`vector` columns take `NULL` / `NOT NULL`, but no other constraints - no keys, no defaults, no
check constraints - and no Always Encrypted, no memory-optimized tables, and no conversions beyond
text/JSON. Plan the schema accordingly.

### Exact kNN scans every row

There is no index behind the search by default. That is fine at sample volumes and honest about
its ceiling; see the RagService section for the `WITH APPROXIMATE` upgrade path.

### Fire-and-forget vs durable jobs

`Task.Run` with exception logging is the simplest background execution. If the process crashes mid-ingestion, chunk data is silently lost. For production, replace with Hangfire or a `Channel<T>`-backed hosted service.
