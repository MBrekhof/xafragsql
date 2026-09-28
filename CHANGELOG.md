# Changelog

Newest first. The README's [Changelog](README.md#changelog) section has a one-line summary of each entry.

## 2026-09-28 — TypeSafe re-ranking (RAG-003)

### Added
- Optional re-ranking of the permitted candidates by [TypeSafe](https://docs.typesafe.ai) before the top 5 go into the prompt (`TypeSafeReranker`). It asks one yes/no question per (question, passage) pair and sorts by the returned probability. It is off by default.
- A **Rerank Settings** screen, visible to administrators only. It holds a single row: an on/off switch and an API key entered in a password popup. The key is stored in the database and never shown again.
- `XafRag.RerankEval`, a console tool that runs 20 labelled questions over the sample documents with and without re-ranking and writes `docs/rerank-eval-report.md`. It reads the key stored in the app.
- Tests: the key is stored, is never shown or logged, and the screen is admin-only. With a key TypeSafe rejects, the chat still answers from the distance order.

### Behaviour
- Only chunks the user may read are sent to TypeSafe. A missing key, a TypeSafe error or more than 5 seconds all fall back to the distance order.
- Result on the sample documents: the right chunk ranked first in 90% of questions instead of 60%. It was already in the top 5 every time, so answers were equally good either way, for about 0.75 s and $0.0007 extra per question. Not worth enabling at this corpus size.

### Fixed
- The test navigation helper no longer collapses a navigation group that is already open.

## 2026-09-27 — Retrieval through XAF security (SEC-001)

### Security
- The chunks live outside XAF, so before this change any user could get any document's content paraphrased back to them. `RagService.SearchAsync` now fetches 4 × `MaxResults` candidates and keeps only those whose parent the current user may read:
  - parents are loaded through a secured Object Space, so type and object permissions apply;
  - the member the chunk text came from (`KnowledgeArticle.Content`, `Document.FileData` → `FileData.Content`) is checked with `CanRead`;
  - chunks without a parent are dropped, and source names are shown only when readable.
- Removed the raw-SQL source-name lookup, which also bypassed security.

### Added
- A `Readers` role and a **Reader** demo user (empty password). Reader may read only articles tagged `public`, cannot see their Content when they are also tagged `nocontent`, and has no access to Documents.
- Test `Retrieval_RespectsObjectAndMemberPermissions`, which checks both the answers and the server's search log. It fails when the filter is bypassed.

### Fixed
- Test harness: negative answer checks passed without actually checking anything, because the helper waited only for the loading bubble. The first login after a cold start could also time out.

## 2026-09-27 — End-to-end smoke tests (TEST-001)

### Added
- `XafRag.Tests`, using Playwright for .NET with NUnit. Each run drops and recreates a separate `XafRagSQL_Tests` database, starts the built server on port 5091 and stops it afterwards. It covers login, RAG Chat in light and dark mode, and ingestion plus a retrieval-grounded answer.

### Fixed
- The RAG Chat empty-state text was unreadable in dark mode. It now uses the theme's subdued text colour instead of a hard-coded `#666`.

## 2026-09-20 — Code-review fixes

### Fixed
- The cascade foreign-key script now runs in one transaction. T-SQL `BEGIN`/`END` groups statements but does not make them atomic.
- Source-name lookups are batched at 1000 ids. SQL Server allows at most 2100 parameters per command.

### Docs
- Corrected the missing-table symptom in the guide: an absent `knowledge_chunks` table raises a database exception on the first search. It does not fall through to "no relevant context found".
- Added a section on adopting a database that already has `knowledge_chunks` but no migration history.
- DevExpress version corrected to 26.1.4. Replaced the inherited PostgreSQL demo video with a SQL Server screenshot.

## 2026-09-20 — Initial release

- Ported from [XafRag](https://github.com/MBrekhof/xafrag) (PostgreSQL + PGVector) to SQL Server 2025's native `VECTOR(1536)` type through EF Core 10.
