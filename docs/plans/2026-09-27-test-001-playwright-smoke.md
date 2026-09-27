# TEST-001 — Playwright smoke test (plan)

Card: TEST-001 (CARD-1765). Goal: automate the end-to-end RAG check that was done by hand on
2026-09-20, so a regression in login → ingest → embed → retrieve → answer turns the suite red.

## Shape

New project `XafRag/XafRag.Tests/XafRag.Tests.csproj`, added to `XafRag.slnx`.

- `net10.0`, `Microsoft.Playwright.NUnit` 1.62.0, `NUnit` 4.x, `NUnit3TestAdapter`,
  `Microsoft.NET.Test.Sdk`, `Microsoft.Data.SqlClient` (DB assertions).
- `ProjectReference` to `XafRag.Blazor.Server` with `ReferenceOutputAssembly="false"`. That only
  enforces build order, so `dotnet test` always runs against a freshly built exe.
- Files:
  - `AppHost.cs` — `[SetUpFixture]`: database reset, `--updateDatabase`, app start/stop.
  - `SmokeTests.cs` — the tests (`PageTest` base).
  - `appsettings.test.json` — base URL/port, test database name, timeouts. No secrets: the only
    credential is the `sa` password that is already public in `appsettings.json` / `docker-compose.yml`.

No page-object layer, `TestSettings` singleton or multi-language login selectors from the
xaf-playwright-testing skill. This is one app with a handful of tests in English, so a page object
would wrap two locators. Add them if the suite grows.

## Fixture (`AppHost`, once per run)

1. **Separate database** `XafRagSQL_Tests` on the same server (`localhost,14333`), so tests never
   touch the dev database. It's passed to the exe as a command-line config override:
   `--ConnectionStrings:ConnectionString=...;Database=XafRagSQL_Tests;...` (`Host.CreateDefaultBuilder`
   reads command-line args into configuration, and `Startup` reads both DbContexts' connection strings
   from `ConnectionStrings:ConnectionString`). No environment variables are used for configuration.
2. **Clean start**: `DROP DATABASE IF EXISTS` (with `SINGLE_USER WITH ROLLBACK IMMEDIATE`) via SqlClient
   against `master`. Every run then starts from a clean database, which is what the card's acceptance asks for.
3. **Create schema**: run `XafRag.Blazor.Server.exe --updateDatabase --silent <override>`, and require
   exit code 0 or 2. This is the known workaround for "refuses to create the database without a debugger".
4. **Start the app**: `XafRag.Blazor.Server.exe --urls http://localhost:5091 <override>`, with the working
   directory set to the exe's folder (content root → `appsettings*.json`, `Model.xafml`). The child process gets
   `ASPNETCORE_ENVIRONMENT=Development` set on its `ProcessStartInfo` only. That's the documented
   host-selector exception: it loads `appsettings.Development.json` (the OpenAI key) and enables static
   web assets. It is not a machine or user env var.
   - Before starting, check the port is free and fail fast if it isn't.
   - Health: poll `GET /` until it returns non-5xx (60 s budget). Capture stdout/stderr to a file in the test
     output dir, and attach it on failure.
   - `Startup` runs `Migrate()` here, which creates `knowledge_chunks`. That's the step the "revert to
     EnsureCreated()" acceptance check targets.
5. **Teardown**: kill the process tree (`Process.Kill(entireProcessTree: true)`), then confirm the port is free.
   The database is left in place for post-mortem inspection and dropped on the next run.

Exe path: `../XafRag.Blazor.Server/bin/<Configuration>/net10.0/XafRag.Blazor.Server.exe`, resolved from the
test assembly's location. Fail with a clear message if it's missing.

## Tests

All tests log in as `Admin` with an empty password, through a shared `[SetUp]`. The Blazor circuit race is handled
by waiting on a stable post-login element with `Expect(...)`, never `Task.Delay`.

1. `Login_ShowsMainWindow` — logged in, navigation visible. No OpenAI needed.
2. `RagChat_Renders_LightAndDark` — open RAG Chat, assert the "Knowledge Base Assistant" empty
   state is visible, and screenshot it in the default theme and in "Blazing Dark" (via the theme switcher).
   Screenshots are attached to the result for eyeballing; there's no pixel assert. No OpenAI needed.
3. `Article_IsIngested_AndAnsweredFromRetrieval` — `[Category("OpenAI")]`:
   - The marker is a fresh GUID-based nonsense fact, e.g. `Project Zebulon-3fa9c1 has the codename
     Quartzfinch-7be204.`, so the model can't know the answer from training.
   - Create a KnowledgeArticle through the UI (Knowledge Base → Knowledge Article → New), with the marker as
     Content, then Save. Ingestion is fired by `KnowledgeArticleIngestionController` on commit.
   - Poll SqlClient (≤ 60 s) for `knowledge_chunks` rows joined to that article's title, and assert
     `DATALENGTH(embedding) = 6152` (1536 × float32 + 8-byte header; confirm the real value on the first run
     and fix the constant if the card's figure is wrong).
   - Open RAG Chat, ask `What is the codename of project Zebulon-3fa9c1?`, and wait for the assistant message.
   - Assert the answer contains `Quartzfinch-7be204`.

Running without a key: `dotnet test --filter "TestCategory!=OpenAI"` runs 1 and 2. When the key is absent, the
OpenAI test fails; it doesn't skip. Silently skipping is how a broken setup reads as green.

## Running

```
cd C:\Projects\XafRagSQL
docker compose up -d
dotnet build XafRag.slnx
pwsh XafRag\XafRag.Tests\bin\Debug\net10.0\playwright.ps1 install chromium   # once
dotnet test XafRag\XafRag.Tests
```

This goes in a short "Tests" section of the README.

## Verification of the acceptance criterion

1. Clean run with the key: all 3 tests green.
2. Temporarily change `Migrate()` to `EnsureCreated()` in `Startup.cs`, rebuild and run. Expect test 3 red
   (no `knowledge_chunks`, so ingestion fails and the poll times out), then revert. It's a local edit only; nothing
   is committed. Report both outputs on the card.
3. Screenshots from test 2, light and dark, inspected by eye.

## Codex plan review — settled (2026-09-27)

Every finding is adopted. These points override the text above:

- **Both child processes** (`--updateDatabase` and the normal start) get the same working directory and
  `ASPNETCORE_ENVIRONMENT=Development`. `Build()` runs before the updater dispatch, and `Startup` builds the
  `OpenAIClient` eagerly, so an empty key throws even on the update path. (blocker)
- **Exe path** is resolved by walking up from the test assembly to the folder that contains `XafRag.slnx`, not via
  a relative `../`. (blocker)
- The connection-string override goes in as the **first** `ArgumentList` entry. The .NET command-line
  parser takes a bare `--flag` followed by another token as a key/value pair. `EasyTestConnectionString` is overridden too.
- **The key is required to start the app at all**, even for the UI-only tests. `[Category("OpenAI")]` still
  exists, but all it does is skip the paid calls. The README says this plainly; the earlier "runs without a key"
  claim is dropped.
- **Ranking must matter.** Create 10 distractor articles first (other made-up projects/codenames), then the
  target article **last**. With `MaxResults` = 5, an unranked `TOP 5` returns the lowest ids and misses the
  target, so dropping the similarity ordering turns the test red.
- **Title is set** to a unique value. Resolve exactly one article id from it, then require ≥ 1 chunk whose
  `content` contains the marker and whose `DATALENGTH(embedding)` = 6152.
- Empty-state assertion targets `.rag-chat-empty h3`, not text that also appears in the header.
- Negative check (Migrate → EnsureCreated): the chunk poll may throw "Invalid object name
  'knowledge_chunks'" immediately rather than time out. Either way it counts as red, as long as the failure
  names the missing table and isn't a login or fixture error.

## Out of scope

- A CI pipeline (needs SQL Server 2025 + an OpenAI key; the shop has no CI deploys anyway).
- Documents (file upload) ingestion. Articles cover the same embed/retrieve path, and file upload adds a
  DevExpress upload-control dance for no extra coverage of the RAG loop. It becomes a follow-up card if wanted.
- A second user/role. That belongs to SEC-001, which builds on this harness.
