using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace XafRag.Tests;

/// <summary>
/// Starts XafRag.Blazor.Server once per test run against a freshly dropped database, and stops it
/// afterwards. The database is kept after the run for inspection and dropped at the next start.
/// </summary>
[SetUpFixture]
public class AppHost
{
    public record Settings(int Port, string SqlServer, string Database);

    public static Settings Config { get; } = JsonSerializer.Deserialize<Settings>(
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appsettings.test.json")))!;

    public static string BaseUrl => $"http://localhost:{Config.Port}";

    /// <summary>Connection string for the test database, for SQL assertions.</summary>
    public static string TestDb => $"{Config.SqlServer};Database={Config.Database};MultipleActiveResultSets=True";

    static Process? _app;
    static readonly List<Process> _started = [];
    static StreamWriter? _log;
    static string LogPath => Path.Combine(TestContext.CurrentContext.WorkDirectory, "app.log");

    [OneTimeSetUp]
    public async Task StartAsync()
    {
        // Interpolated into T-SQL below, so only plain identifiers.
        Assert.That(Config.Database, Does.Match("^[A-Za-z0-9_]+$"), "Database must be a plain identifier");
        var exe = FindServerExe();
        await DropDatabaseAsync();

        // The app refuses to create its database without a debugger attached, so the XAF updater
        // runs first as its own process. 0 = updated, 2 = nothing to do.
        var update = Start(exe, "--updateDatabase", "--silent");
        await update.WaitForExitAsync().WaitAsync(TimeSpan.FromMinutes(3));
        Assert.That(update.ExitCode, Is.AnyOf(0, 2), $"--updateDatabase failed, see {LogPath}");

        Assert.That(IsPortFree(Config.Port), Is.True, $"Port {Config.Port} is already in use");
        _app = Start(exe, "--urls", BaseUrl);
        await WaitUntilServingAsync();
    }

    [OneTimeTearDown]
    public void Stop()
    {
        // Every child, not only the server: a timed-out updater must not outlive the run.
        foreach (var p in _started.Where(p => !p.HasExited))
        {
            p.Kill(entireProcessTree: true);
            p.WaitForExit(10_000);
        }
        TestContext.AddTestAttachment(LogPath, "Server output");
        Assert.That(IsPortFree(Config.Port), Is.True, $"Server still holds port {Config.Port} after shutdown");
    }

    static Process Start(string exe, params string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        // First, so no bare flag before it can swallow it as its value.
        var conn = $"{Config.SqlServer};Database={Config.Database};MultipleActiveResultSets=True";
        psi.ArgumentList.Add($"--ConnectionStrings:ConnectionString={conn}");
        psi.ArgumentList.Add($"--ConnectionStrings:EasyTestConnectionString={conn}");
        foreach (var a in args) psi.ArgumentList.Add(a);
        // Host selector for this child only: loads appsettings.Development.json (the OpenAI key,
        // which Startup needs even on the --updateDatabase path) and serves static web assets.
        psi.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";

        var log = _log ??= new StreamWriter(LogPath, append: false) { AutoFlush = true };
        lock (log) log.WriteLine($"==== {Path.GetFileName(exe)} {string.Join(' ', args)}");
        var p = Process.Start(psi)!;
        _started.Add(p);
        p.OutputDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (log) log.WriteLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        return p;
    }

    static async Task WaitUntilServingAsync()
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var deadline = DateTime.UtcNow.AddSeconds(90);
        while (DateTime.UtcNow < deadline)
        {
            if (_app!.HasExited)
                Assert.Fail($"Server exited with code {_app.ExitCode} during startup, see {LogPath}");
            try
            {
                var resp = await http.GetAsync(BaseUrl);
                if ((int)resp.StatusCode < 500) return;
            }
            catch (HttpRequestException) { }
            catch (TaskCanceledException) { }
            await Task.Delay(1000);
        }
        Assert.Fail($"Server did not answer on {BaseUrl} within 90 s, see {LogPath}");
    }

    static async Task DropDatabaseAsync()
    {
        await using var conn = new SqlConnection($"{Config.SqlServer};Database=master");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            IF DB_ID(N'{Config.Database}') IS NOT NULL
            BEGIN
                ALTER DATABASE [{Config.Database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE;
                DROP DATABASE [{Config.Database}];
            END
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    static string FindServerExe()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "XafRag.slnx")))
            dir = dir.Parent;
        Assert.That(dir, Is.Not.Null, "XafRag.slnx not found above the test output folder");

        // Same configuration as the test build: bin/<Configuration>/<tfm>/
        var tfmDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var exe = Path.Combine(dir!.FullName, "XafRag", "XafRag.Blazor.Server", "bin",
            tfmDir.Parent!.Name, tfmDir.Name, "XafRag.Blazor.Server.exe");
        Assert.That(File.Exists(exe), Is.True, $"Server not built: {exe}");
        return exe;
    }

    static bool IsPortFree(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().All(e => e.Port != port);
}
