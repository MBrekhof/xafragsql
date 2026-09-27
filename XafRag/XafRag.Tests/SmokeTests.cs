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

    ILocator KnowledgeBaseGroup => Page.GetByRole(AriaRole.Application, new() { Name = "Knowledge Base" });

    [SetUp]
    public async Task LogInAsync()
    {
        Page.SetDefaultTimeout(30_000);
        await Page.GotoAsync(AppHost.BaseUrl, new() { WaitUntil = WaitUntilState.DOMContentLoaded });
        await Page.GetByRole(AriaRole.Textbox, new() { Name = "User Name" }).FillAsync("Admin");
        await Page.GetByRole(AriaRole.Button, new() { Name = "Log In" }).ClickAsync();
        // The navigation pane only renders once the circuit is up and the user is signed in.
        await Expect(KnowledgeBaseGroup).ToBeVisibleAsync();
    }

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
        await OpenAsync("RAG Chat");
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

        await OpenAsync("Knowledge Article");
        await Page.GetByRole(AriaRole.Button, new() { Name = "New" }).ClickAsync();
        var title = Page.GetByRole(AriaRole.Textbox, new() { Name = "Title" });
        var content = Page.GetByRole(AriaRole.Textbox, new() { Name = "Content" });
        foreach (var a in articles)
        {
            await Expect(title).ToBeEmptyAsync();
            await title.FillAsync(a.Title);
            await content.FillAsync($"Project {a.Project} has the codename {a.Codename}.");
            var last = a == target;
            await Page.GetByRole(AriaRole.Button, new() { Name = last ? "Save" : "Save and New", Exact = true }).ClickAsync();
        }
        await Expect(Page.GetByRole(AriaRole.Button, new() { Name = "Delete" })).ToBeEnabledAsync();

        var chunks = await WaitForChunksAsync(articles.Select(a => a.Title).ToArray());
        var targetChunks = chunks.Where(c => c.Title == target.Title).ToList();
        Assert.That(targetChunks, Is.Not.Empty);
        Assert.That(targetChunks.Any(c => c.Content.Contains(target.Codename)), Is.True,
            "the target article's chunk does not contain its text");
        // vector(1536): 1536 float32 values + an 8-byte header.
        Assert.That(chunks.Select(c => c.EmbeddingBytes), Is.All.EqualTo(6152), "embedding missing or wrong size");

        await OpenAsync("RAG Chat");
        var input = Page.Locator(".rag-chat textarea");
        await input.FillAsync($"What is the codename of project {target.Project}?");
        await input.PressAsync("Enter");

        var reply = Page.Locator(".dxbl-chatui-message-assistant, .dxbl-chatui-message-error").Last;
        await Expect(reply).ToBeVisibleAsync(new() { Timeout = 90_000 });
        await Expect(Page.Locator(".dxbl-chatui-message-error")).ToHaveCountAsync(0);
        await Expect(reply).ToContainTextAsync(target.Codename);
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

    async Task OpenAsync(string navItem)
    {
        var item = Page.GetByRole(AriaRole.Treeitem, new() { Name = navItem, Exact = true });
        if (!await item.IsVisibleAsync())
            await KnowledgeBaseGroup.ClickAsync();
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

    async Task ScreenshotAsync(string suffix)
    {
        var path = Path.Combine(TestContext.CurrentContext.WorkDirectory, "screenshots",
            $"{TestContext.CurrentContext.Test.Name}_{suffix}.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await Page.ScreenshotAsync(new() { Path = path, FullPage = true, Animations = ScreenshotAnimations.Disabled });
        TestContext.AddTestAttachment(path);
    }
}
