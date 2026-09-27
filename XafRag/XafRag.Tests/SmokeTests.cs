using Microsoft.Data.SqlClient;
using Microsoft.Playwright;
using Microsoft.Playwright.NUnit;

namespace XafRag.Tests;

/// <summary>
/// End-to-end smoke test of the RAG loop: login, article ingestion, embeddings written, and an answer
/// that can only have come from retrieval. Needs SQL Server 2025 (docker compose) and a valid OpenAI
/// key in the server's appsettings.Development.json; see the README's Tests section.
/// </summary>
[NonParallelizable]
public class SmokeTests : PageTest
{
    public override BrowserNewContextOptions ContextOptions() => new()
    {
        ViewportSize = new() { Width = 1600, Height = 1000 },
    };

    [SetUp]
    public Task LogInAsAdminAsync() => LogInAsync(Page, "Admin");

    async Task LogInAsync(IPage page, string user)
    {
        page.SetDefaultTimeout(30_000);
        await page.GotoAsync(AppHost.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await page.GetByRole(AriaRole.Textbox, new() { Name = "User Name" }).FillAsync(user);
        await page.GetByRole(AriaRole.Button, new() { Name = "Log In" }).ClickAsync();
        // The navigation pane only renders once the circuit is up and the user is signed in.
        // Expect has its own 5 s default; the first login after a cold start takes longer.
        await Expect(KnowledgeBaseGroup(page)).ToBeVisibleAsync(new() { Timeout = 30_000 });
    }

    static ILocator KnowledgeBaseGroup(IPage page) =>
        page.GetByRole(AriaRole.Application, new() { Name = "Knowledge Base" });

    [TearDown]
    public async Task ScreenshotOnFailureAsync()
    {
        if (TestContext.CurrentContext.Result.Outcome.Status == NUnit.Framework.Interfaces.TestStatus.Failed)
            await ScreenshotAsync("failure");
    }

    [Test]
    public async Task Login_ShowsMainWindow()
    {
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Admin" })).ToBeVisibleAsync();
    }

    [Test]
    public async Task RagChat_Renders_LightAndDark()
    {
        await OpenAsync(Page, "RAG Chat");
        await Expect(Page.Locator(".rag-chat-empty h3")).ToHaveTextAsync("Knowledge Base Assistant");

        try
        {
            await SetThemeModeAsync("light");
            await ScreenshotAsync("light");
            await SetThemeModeAsync("dark");
            await ScreenshotAsync("dark");
        }
        finally
        {
            // The mode is stored per user, and every test logs in as Admin.
            await SetThemeModeAsync("light");
        }
    }

    [Test]
    [Category("OpenAI")]
    public async Task Article_IsIngested_AndAnsweredFromRetrieval()
    {
        var run = Guid.NewGuid().ToString("N")[..6];
        string[] names = ["Heron", "Maple", "Falcon", "Cedar", "Otter", "Juniper", "Kestrel", "Willow", "Badger", "Lynx", "Zebulon"];
        var articles = names.Select(n => (
            Title: $"Smoke {run} {n}",
            Codename: $"{n[..3]}finch-{Guid.NewGuid().ToString("N")[..6]}",
            Project: $"{n}-{run}")).ToList();
        // The target goes in last, so it gets the highest id: with more articles than
        // Rag:MaxResults (5), a search that stopped ranking by distance would return low ids and miss it.
        var target = articles[^1];

        await CreateArticlesAsync(articles.Select(a =>
            (a.Title, $"Project {a.Project} has the codename {a.Codename}.", (string?)null)).ToList());

        var chunks = await WaitForChunksAsync(articles.Select(a => a.Title).ToArray());
        var targetChunks = chunks.Where(c => c.Title == target.Title).ToList();
        Assert.That(targetChunks, Is.Not.Empty);
        Assert.That(targetChunks.Any(c => c.Content.Contains(target.Codename)), Is.True,
            "the target article's chunk does not contain its text");
        // vector(1536): 1536 float32 values + an 8-byte header.
        Assert.That(chunks.Select(c => c.EmbeddingBytes), Is.All.EqualTo(6152), "embedding missing or wrong size");

        await OpenAsync(Page, "RAG Chat");
        var reply = await AskAsync(Page, $"What is the codename of project {target.Project}?");
        await Expect(reply).ToContainTextAsync(target.Codename);
    }

    [Test]
    [Category("OpenAI")]
    public async Task Retrieval_RespectsObjectAndMemberPermissions()
    {
        // Reader (seeded by Updater) may read articles whose Tags contain "public", but not the
        // Content of those also tagged "nocontent". Retrieval must follow the same rules.
        var run = Guid.NewGuid().ToString("N")[..6];
        string Code() => Guid.NewGuid().ToString("N")[..6];
        var open = (Title: $"Sec {run} Open", Project: $"Aurora-{run}", Codename: $"Opal-{Code()}", Tags: (string?)"public");
        var secret = (Title: $"Sec {run} Secret", Project: $"Borealis-{run}", Codename: $"Onyx-{Code()}", Tags: (string?)null);
        var masked = (Title: $"Sec {run} Masked", Project: $"Cygnus-{run}", Codename: $"Jade-{Code()}", Tags: (string?)"public nocontent");
        var all = new[] { open, secret, masked };

        await CreateArticlesAsync(all.Select(a =>
            (a.Title, $"Project {a.Project} has the codename {a.Codename}.", a.Tags)).ToList());
        await WaitForChunksAsync(all.Select(a => a.Title).ToArray());

        // Control: all three are retrievable, so a missing answer for Reader below is the filter.
        await OpenAsync(Page, "RAG Chat");
        foreach (var a in all)
            await Expect(await AskAsync(Page, $"What is the codename of project {a.Project}?")).ToContainTextAsync(a.Codename);

        await using var readerContext = await Browser.NewContextAsync(ContextOptions());
        var reader = await readerContext.NewPageAsync();
        try
        {
        await LogInAsync(reader, "Reader");
        // Reader can open Masked (only its Content is hidden), so excluding it below is the member
        // check at work, not the object check.
        await OpenAsync(reader, "Knowledge Article");
        await Expect(reader.GetByText(open.Title, new() { Exact = true })).ToBeVisibleAsync();
        await Expect(reader.GetByText(masked.Title, new() { Exact = true })).ToBeVisibleAsync();
        await Expect(reader.GetByText(secret.Title, new() { Exact = true })).ToHaveCountAsync(0);
        await OpenAsync(reader, "RAG Chat");

        await Expect(await AskAsync(reader, $"What is the codename of project {open.Project}?")).ToContainTextAsync(open.Codename);
        foreach (var hidden in new[] { secret, masked })
        {
            var question = $"What is the codename of project {hidden.Project}?";
            var reply = await AskAsync(reader, question);
            await Expect(reply).Not.ToContainTextAsync(hidden.Codename);
            // The answer not mentioning it is not enough: it must not have reached the prompt.
            // The same question was asked once by Admin above; Reader's search is the second line.
            // Server output reaches the log asynchronously, so wait for it.
            string[] lines = [];
            for (var i = 0; i < 20; i++)
            {
                lines = AppHost.ReadLog().Split('\n').Where(l => l.Contains($"RAG search for '{question}'")).ToArray();
                if (lines.Length >= 2) break;
                await Task.Delay(500);
            }
            Assert.That(lines, Has.Length.EqualTo(2), "Reader's search was not logged");
            // Exact, not "does not contain the title": Reader may read only the Open article (the
            // other tests' articles are untagged), so anything else in the prompt is a leak.
            Assert.That(lines[1].TrimEnd(), Does.EndWith($"returned 1 results: {open.Title}"), "a hidden article reached the prompt");
        }
        }
        catch
        {
            await ScreenshotAsync("reader", reader);
            throw;
        }
    }

    /// <summary>Sends a question and returns the reply bubble; fails on an error bubble.</summary>
    async Task<ILocator> AskAsync(IPage page, string question)
    {
        var replies = page.Locator(".dxbl-chatui-message-assistant, .dxbl-chatui-message-error");
        var before = await replies.CountAsync();
        var input = page.Locator(".rag-chat textarea");
        await input.FillAsync(question);
        // Click rather than Enter: Send enables only once the typed text has reached the server.
        await page.Locator(".rag-chat button[title=Send]").ClickAsync();

        // The "Searching knowledge base..." indicator is itself an assistant bubble: wait for it to
        // appear and go again, or the caller asserts on the placeholder instead of the answer.
        var loading = page.Locator(".rag-chat").GetByText("Searching knowledge base...");
        await Expect(replies).ToHaveCountAsync(before + 1, new() { Timeout = 90_000 });
        await Expect(loading).ToHaveCountAsync(0, new() { Timeout = 90_000 });
        await Expect(replies).ToHaveCountAsync(before + 1);
        await Expect(page.Locator(".dxbl-chatui-message-error")).ToHaveCountAsync(0);
        return replies.Last;
    }

    async Task CreateArticlesAsync(List<(string Title, string Content, string? Tags)> articles)
    {
        await OpenAsync(Page, "Knowledge Article");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New" }).ClickAsync();
        var title = Page.GetByRole(AriaRole.Textbox, new() { Name = "Title" });
        for (var i = 0; i < articles.Count; i++)
        {
            await Expect(title).ToBeEmptyAsync();
            await title.FillAsync(articles[i].Title);
            await Page.GetByRole(AriaRole.Textbox, new() { Name = "Content" }).FillAsync(articles[i].Content);
            if (articles[i].Tags is { } tags)
                await Page.GetByRole(AriaRole.Textbox, new() { Name = "Tags" }).FillAsync(tags);
            var last = i == articles.Count - 1;
            await Page.GetByRole(AriaRole.Button, new() { Name = last ? "Save" : "Save and New", Exact = true }).ClickAsync();
        }
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Delete" })).ToBeEnabledAsync();
    }

    record Chunk(string Title, string Content, long EmbeddingBytes);

    /// <summary>Waits until every article has at least one chunk, then returns all their chunks.</summary>
    static async Task<List<Chunk>> WaitForChunksAsync(string[] titles)
    {
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (true)
        {
            var chunks = new List<Chunk>();
            await using (var conn = new SqlConnection(AppHost.TestDb))
            {
                await conn.OpenAsync();
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    SELECT a.Title, kc.content, CAST(ISNULL(DATALENGTH(kc.embedding), 0) AS bigint)
                    FROM knowledge_chunks kc JOIN KnowledgeArticles a ON a.Id = kc.knowledge_article_id
                    WHERE a.Title IN (SELECT value FROM OPENJSON(@titles))
                    """;
                cmd.Parameters.AddWithValue("@titles", System.Text.Json.JsonSerializer.Serialize(titles));
                await using var r = await cmd.ExecuteReaderAsync();
                while (await r.ReadAsync())
                    chunks.Add(new(r.GetString(0), r.GetString(1), r.GetInt64(2)));
            }
            var missing = titles.Except(chunks.Select(c => c.Title)).ToList();
            if (missing.Count == 0) return chunks;
            if (DateTime.UtcNow > deadline)
                Assert.Fail($"No chunks after 90 s for: {string.Join(", ", missing)} (see the server log attachment)");
            await Task.Delay(1000);
        }
    }

    static async Task OpenAsync(IPage page, string navItem)
    {
        var item = page.GetByRole(AriaRole.Treeitem, new() { Name = navItem, Exact = true });
        if (!await item.IsVisibleAsync())
            await KnowledgeBaseGroup(page).ClickAsync();
        await item.ClickAsync();
    }

    async Task SetThemeModeAsync(string mode)
    {
        await Page.GetByRole(AriaRole.Button, new() { Name = "Settings" }).ClickAsync();
        var button = Page.Locator($".themeswitcher-fluent-mode-{mode}");
        await Expect(button).ToBeVisibleAsync();
        // The current mode's button is disabled. Switching closes the panel by itself; otherwise close it.
        if (await button.IsEnabledAsync())
            await button.ClickAsync();
        else
            await Page.GetByRole(AriaRole.Button, new() { Name = "Settings" }).ClickAsync();
        await Expect(button).ToBeHiddenAsync();
        // Loaded, not merely linked: .sheet is null until the stylesheet has been fetched and parsed.
        await Page.WaitForFunctionAsync(
            $"() => document.querySelector(\"link[href*='/{mode}.min.css']\")?.sheet != null");
    }

    async Task ScreenshotAsync(string suffix, IPage? page = null)
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "screenshots",
            $"{TestContext.CurrentContext.Test.Name}_{suffix}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await (page ?? Page).ScreenshotAsync(new() { Path = path, FullPage = true, Animations = ScreenshotAnimations.Disabled });
        TestContext.AddTestAttachment(path);
    }
}
