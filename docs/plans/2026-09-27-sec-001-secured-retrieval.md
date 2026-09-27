# SEC-001 — RAG retrieval through XAF security (plan)

Card: SEC-001 (CARD-1764). Option 2 from the card: vector search first, then filter the
candidates' parents through a **secured** Object Space, so XAF's type, object and member
permissions decide what reaches the prompt.

## The leak today (`Services/RagService.cs`)

1. `SearchAsync` queries `RagDbContext.KnowledgeChunks` directly: no user, no permissions.
2. `ResolveNamesAsync` reads `Documents.FileName` / `KnowledgeArticles.Title` with raw SQL,
   which also ignores permissions.
3. Chunk text goes into the system prompt verbatim, so a user without read access to a document
   gets its content paraphrased back.

## Change

### `RagService.SearchAsync`

1. Vector search as now, but fetch `MaxResults * CandidateMultiplier` candidates (multiplier 4,
   a constant). `// ponytail:` comment: if the user can read only a small share of the nearest
   chunks, the top-N under-fills. The upgrade is a permission-aware pre-filter (option 3 on the card).
2. Collect the distinct parent ids per source type.
3. Open `IObjectSpaceFactory.CreateObjectSpace<KnowledgeArticle>()` (plus `<Document>`). This is
   the documented DI service: it secures queries for the logged-in user and throws when nobody
   is logged in (dxdocs 403669 / `IObjectSpaceFactory`).
   `os.GetObjectsQuery<KnowledgeArticle>().Where(a => ids.Contains(a.Id))` returns only the
   articles the user may read (type + object permissions); same for `Document`.
4. **Member level**: the chunk text *is* `KnowledgeArticle.Content` / the `Document.FileData`
   file, so the parent being readable isn't enough. Per surviving parent, require
   `security.CanRead(type, os, key, "Content" | "FileData")`, using `ISecurityProvider.GetSecurity()`
   as `IRequestSecurityStrategy` (`IsGrantedExtensions.CanRead`, dxdocs). The source name
   (`Title` / `FileName`) is used only if that member is readable too; otherwise it's "restricted source".
5. Keep chunks whose parent passed, in distance order, `Take(MaxResults)`.
6. `ResolveNamesAsync` and its raw SQL are **deleted**: names now come from the secured query.

No exception path is added. A user who can see nothing gets "No relevant context found", and
the model says it doesn't know. That's the card's "degrades honestly".

### Demo user (`Updater.cs`, inside the existing `#if !RELEASE`)

A new role, **Readers**, and user **Reader** (empty password, like the others):

- `KnowledgeArticle`: Read only, object permission `[Tags] Like '%public%'`.
- `Document`: no permission (type-level denied by the default policy).
- `RagChatHolder`: read, plus navigation to Knowledge Base → Knowledge Article and RAG Chat.
- The own-user and model-difference permissions that `Default` has (needed to log in and store UI settings).

This makes the security model visible in the sample: log in as Reader, and RAG answers from public articles only.

### Tests (`SmokeTests.cs`)

New `[Category("OpenAI")]` test `Retrieval_RespectsObjectPermissions`:

1. As Admin, create a `public`-tagged article with codename P, and an untagged article with
   codename S, then wait for both chunks.
2. Log in as Reader in a **new browser context**. Ask about S: there must be no error bubble, and the
   answer must **not** contain S. Ask about P: the answer must contain P (proves Reader's chat works
   at all, so the S result isn't just a broken chat).
3. Admin still gets S. That's already covered by the existing ingestion test.

Refactor: `LogInAsync(IPage, user)` so both contexts can use it.

Negative check (local, not committed): in `SearchAsync`, swap the secured factory for
`INonSecuredObjectSpaceFactory`. The new test must go red, with S in Reader's answer.

### Docs

- README: a "Security" subsection under How It Works (retrieval respects object and member
  permissions; log in as Reader to see it), the retrieval pipeline diagram gets a "security
  filter" step, and the tests table gets the new test.
- `how_to_implement.md`: the RagService section replaces "source name resolution via raw SQL"
  with the secured filter (code excerpt + over-fetch caveat). Step 9 gets the Reader role
  as the example of object-level permissions affecting RAG.

## Codex plan review — settled (2026-09-27)

Confirmed from DevExpress source: `IObjectSpaceFactory`/`ISecurityProvider` are circuit-scoped;
`GetObjectsQuery` injects the permission predicate before our `Where`; the `CanRead` overload
exists and the cast holds. Changes to the plan:

- **Document content is `FileData.Content`** (blocker). Require `CanRead(Document, FileData)` **and**
  `CanRead(typeof(FileData), os, fileDataKey, "Content")` on the referenced FileData object.
  Chunks with **no** parent id, or an unknown source type, are dropped (fail closed).
- **Readers role** = conditional object grants only, never the Default role (Default has CRUD on both types).
- **Negative check** = skip the whole filter step (keep every candidate), not just a factory swap.
  The member check would otherwise still catch it.
- **Prompt-level assertion**: `SearchAsync` logs the source titles it kept
  (`... returned {Count} results: {Sources}`). The test asserts on the server log that Reader's
  search kept neither S nor M, as well as on the answer text.
- **Admin control in the same test**: Admin asks about S and gets S, proving S is retrievable.
- **Member-level coverage**: the Readers role denies `Content` on articles whose Tags contain `nocontent`.
  A third article M (`public nocontent`) is readable but must be excluded.
- Tag matching is substring (`Like '%public%'`), which is fine for a demo; the README says "tag containing".
- Not covered by tests: the Document/FileData path (it needs a file upload through the UI). It's
  code-reviewed only, and the card conclusion will say so.

## Out of scope

- The background `IngestionService` keeps writing chunks non-secured, and updates
  `Document.Status` by SQL. It runs server-side on a commit the user was already allowed to make.
- DOC-001 (warning-only card) becomes redundant once this lands. That's for the user to
  decide, not closed silently.
- xafrag (Postgres) back-port: a separate card if wanted.
