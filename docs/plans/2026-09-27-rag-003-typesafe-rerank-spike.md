# RAG-003 — TypeSafe rerank spike (plan)

Card: RAG-003 (CARD-1882). The card said "full plan on the card", but no plan existed; this is it.

**Question the spike answers:** does re-scoring the vector-search candidates with TypeSafe (Jev)
put better chunks into the prompt than cosine distance alone, and what does it cost in latency
and money? The outcome is a measured yes/no recorded on the card, not a feature launch.

> **Superseded (user decision, 2026-09-27):** the key is stored **as entered** in
> `RerankSettings.ApiKey`, like duetGPT's admin-managed provider keys: no ASP.NET Data Protection,
> no user-secrets. Everything below about `EncryptedApiKey`, `IDataProtector`, key rings and
> user-secrets describes the first draft, not the code. The eval reads the key from the app's
> dev database. See "Result" at the end.

## TypeSafe, as the live docs describe it (docs.typesafe.ai, read 2026-09-27)

- `POST https://api.typesafe.ai/v1/systemone`, `Authorization: Bearer <key>`, body
  `{ state, model: "jev-latest", questions: { id: { type: "noul", instructions, criteria: { true, false } } } }`.
  Response `answers.<id>.noul` ∈ [0,1] plus `usage.input_tokens/output_tokens`.
- The rerank cookbook uses exactly this shape: one Noul per (query, candidate) pair, with state
  `{ query, candidate }`, calls run concurrently, sorted by noul. Their example priced 1,200
  calls at $0.06 (jev-1.12, $0.042 per 1M input tokens, output free).
- 401 bad key, 422 validation, 429/529 → retry with exponential backoff (there's no C# SDK, so
  we do it ourselves).

## Design

### 1. `TypeSafeReranker` (Blazor.Server/Services)

A thin typed `HttpClient` wrapper, no SDK:

- `Task<RerankResult> ScoreAsync(string question, IReadOnlyList<string> passages, string apiKey, ct)`.
  One request per passage, all in flight at once (capped at 8 with a `SemaphoreSlim`), returning
  one noul per passage plus summed tokens and wall-clock time.
- The question (Noul), worded for our case:
  - instructions: "Does `passage` contain information that helps answer `question`?"
  - true: "The passage states facts that directly answer the question or a necessary part of it."
  - false: "The passage is only on a related topic, or does not address what the question asks."
- Retry on 429/529 only: 3 attempts at 0.5 s, 1 s and 2 s. Anything else throws.

### 2. Hook in `RagService.SearchAsync`

The current pipeline is vector top 20 → security filter → first 5. It becomes:
vector top 20 → **security filter (all readable, no early break)** → *if rerank is on:* score
the readable ones with TypeSafe and sort by noul (stable, so ties keep the distance order) → first 5.

- The security filter runs **before** reranking, so only chunks the user may read are ever sent
  to TypeSafe, a third party.
- **Fail open to the vector order**: if TypeSafe errors or times out (5 s budget), log a warning and
  keep the distance order. A rerank outage must not break the chat.
- The log line gets the rerank outcome: `reranked in {ms} ms, {tokens} tokens` or `rerank off/failed`.

### 3. Settings: key encrypted in the app database (card requirement)

The same pattern as duetGPT's `provider_settings`:

- XAF entity `RerankSettings` (a single row, seeded by the Updater): `Enabled` (bool),
  `EncryptedApiKey` (string, hidden in the UI), `[NotMapped] ApiKey` (write-only input),
  and `IsConfigured` (computed).
- A `RerankSettingsController` (DetailView) encrypts on `ObjectSpace.Committing` with
  `IDataProtector` (purpose `"XafRag.TypeSafe.ApiKey"`), then clears the plaintext. The key
  never gets written unencrypted.
- `RagService` reads the row through the **non-secured** object space factory (a server-side
  read of server configuration: a Reader chatting must still get reranking) and decrypts.
- Permissions: only Administrators can see or edit the settings (deny-by-default already covers
  Default/Readers; they get no grant).
- The Data Protection key ring stays at the ASP.NET default location
  (`%LOCALAPPDATA%\ASP.NET\DataProtection-Keys`) with `SetApplicationName("XafRag")`, so it
  survives rebuilds. The README notes that a container deployment has to persist that folder,
  or the stored key becomes undecryptable.

### 4. Evaluation (the actual spike)

`XafRag/XafRag.RerankEval`, a console project. It references Blazor.Server for
`ChunkingService`/`EmbeddingService`/`TypeSafeReranker` and runs **without XAF security**
(the eval is about ranking, not permissions):

1. Its own database `XafRagSQL_Eval`, created by `RagDbContext.Migrate()`. It ingests the 10
   sample documents in `docs/*.md` with the real chunker and embedder. The source id is the file's
   index; `document_id` is left null, since there's no Documents table in the eval DB.
2. `rerank-eval.json`: 20 questions written against the sample docs, each with its gold
   document (and a gold phrase that must appear in the gold chunk). A mix of easy, paraphrased
   and cross-document-confusable questions (Fermi paradox vs dark forest, EF querying vs relationships).
3. For each question: vector top 20 (the same query as `SearchAsync`), then reranked. Record the
   rank of the first gold chunk in both orders, hit@1, hit@5, MRR, TypeSafe latency, and tokens.
4. Answer quality: generate the gpt-4o answer from the top 5 of **both** orders (same prompt as
   `AskAsync`) and write both side by side into `docs/rerank-eval-report.md`, for a human read.
   Totals go at the top; cost uses TypeSafe's published price, plus OpenAI chat tokens from `usage`.
5. Keys: OpenAI from the server's `appsettings.Development.json` (the file already used for dev),
   TypeSafe from **`dotnet user-secrets`** on the eval project (a dev-time key: never an env var,
   never in a file in the repo). The app itself uses the DB-stored key.

The eval is not part of `dotnet test`; it's run by hand, since it costs money and takes minutes.

### 5. Tests

- `Rerank_Settings_EncryptsKey` (Playwright, no OpenAI): Admin opens Rerank Settings, types a
  dummy key, and saves. The DB column must be non-empty and must **not** contain the dummy
  plaintext, and the UI must show `IsConfigured`. Reader has no navigation item for it.
- No automated rerank test against the real TypeSafe API. Correctness is measured by the eval,
  and the fail-open path is covered by the settings test's dummy key: a chat with rerank on and
  an invalid key must still answer, and the log must show `rerank failed`.

## Codex plan review — settled (2026-09-27)

- **Key entry (blocker):** XAF doesn't mark the object space modified for a `[NotMapped]` member
  (`nonPersistentChangesEnabled = false`), so a key-only edit would leave Save disabled. Replaced
  with a **"Set API Key" PopupWindowShowAction**: the popup shows a non-persistent
  `ApiKeyInput { [PasswordPropertyText] Key }`, and Execute encrypts it into
  `RerankSettings.EncryptedApiKey` (a persistent member, so the object space is modified) and
  commits. A "Clear API Key" SimpleAction removes it. `RerankSettings` has no plaintext member at all.
- **Schema:** XAF's `DatabaseUpdater` runs `EFCoreDatabaseSchemaUpdater` (a model diff), so an
  existing database gets the new table on `--updateDatabase` or a debug start. No migration needed;
  the README says to run the update.
- **Settings read:** one non-secured object space per read, disposed, values copied before any HTTP work.
- **Rerank contract:** scores are kept by input index. The list is re-sorted only when **every**
  passage scored, otherwise it falls back to the **filtered** distance order. A single 5 s linked CTS
  covers the semaphore wait, requests and retries. Caller cancellation is rethrown, never treated as
  a rerank failure. Settings or decryption failures also fall back.
- **Fail-open test** needs OpenAI (embedding + chat), so it's `[Category("OpenAI")]`. It proves the
  fallback for a rejected key (401) only.
- **Eval:** it does **not** call `SearchAsync`, whose parent/security filter would drop every eval
  chunk. It runs the same vector expression over its own database, with
  `knowledge_article_id` = file index as the identity map (the eval DB has no parent tables, so
  there's no FK). The corpus is pinned to the 10 sample files (no `how_to_implement.md`). Gold =
  the chunks containing the gold phrase, checked against a dump of the actual chunks before
  running. A gold chunk missing from the candidates counts as a miss, and candidate recall is
  reported separately from rerank gain. A static estimate is ~35 chunks, so 20 candidates cover
  half the corpus: the report says it's a small-corpus ranking comparison, not a recall claim.

## Out of scope

- Making rerank the default. That depends on the eval result, and the user decides.
- Other rerankers (Cohere, cross-encoders) for comparison. A follow-up card if the spike is inconclusive.
- Changing `MaxResults`/candidate counts beyond what the spike needs.

## Needs from the user

- A TypeSafe API key. The user enters it via `dotnet user-secrets` for the eval and through the
  Rerank Settings screen for the app. It never goes through the chat transcript.

## Result (2026-09-28, full numbers in docs/rerank-eval-report.md)

20 questions over the 10 sample documents (35 chunks), top 20 candidates, top 5 into the prompt:

| | distance | reranked |
|---|---|---|
| gold chunk ranked first | 60% | 90% |
| confusable questions, ranked first | 17% | 67% |
| gold chunk in the top 5 (what reaches the prompt) | 100% | 100% |
| MRR | 0.76 | 0.95 |

- **Ranking improves, answers don't.** Because the gold chunk was already in the top 5 for every
  question, gpt-4o answered correctly from both orders. Read side by side, no answer went from wrong
  to right. One (q18) quoted the relationships doc exactly with distance order, and the reranked one
  borrowed wording from the querying doc.
- **Cost and latency:** $0.013 for 400 scored pairs (~$0.0007 per question, published jev price);
  median 739 ms and max 876 ms per question, never over the app's 5 s budget.
- **Verdict:** not worth turning on for a corpus this size. It would only pay off when the right chunk
  falls *outside* the top 5 (a much larger knowledge base, or a smaller MaxResults), which this
  eval can't show. The rerank stays in the code, off by default, with its admin switch.
