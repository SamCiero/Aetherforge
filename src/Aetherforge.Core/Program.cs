using System.Diagnostics;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

var builder = WebApplication.CreateBuilder(args);

// WSL-local bind (reachability gate tests Windows -> WSL)
builder.WebHost.UseUrls("http://127.0.0.1:8484");

var app = builder.Build();

app.MapGet("/v1/status", async () =>
{
    var capturedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    // WSL-view paths
    var settingsPath = "/mnt/d/Aetherforge/config/settings.yaml";
    var pinnedPath   = "/mnt/d/Aetherforge/config/pinned.yaml";
    var dbPath       = "/var/lib/aetherforge/conversations.sqlite";

    // Ollama reachability + version
    var ollamaReachable = false;
    string? ollamaVersion = null;
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var ver = await http.GetFromJsonAsync<OllamaVersion>("http://127.0.0.1:11434/api/version");
        ollamaReachable = ver is not null && !string.IsNullOrWhiteSpace(ver.version);
        ollamaVersion = ver?.version;
    }
    catch { }

    // Ollama models dir from systemd metadata
    string? ollamaModelsDir = null;
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = "-lc \"systemctl show ollama -p Environment --value 2>/dev/null || true\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi);
        if (p is not null && p.WaitForExit(1500))
        {
            var stdout = (p.StandardOutput.ReadToEnd() ?? "");
            foreach (var part in stdout.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                if (part.StartsWith("OLLAMA_MODELS=", StringComparison.Ordinal))
                {
                    ollamaModelsDir = part["OLLAMA_MODELS=".Length..].Trim();
                    break;
                }
            }
        }
    }
    catch { }

    // DB bootstrap + health
    var dbHealthy = false;
    string? dbError = null;
    try
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        await using var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync();

        await ExecAsync(conn, "PRAGMA journal_mode=WAL;");
        await ExecAsync(conn, "PRAGMA synchronous=NORMAL;");
        await ExecAsync(conn, "PRAGMA busy_timeout=5000;");

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS meta (
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            """);

        await ExecAsync(conn, """
            INSERT INTO meta(key,value) VALUES('schema_version','1')
              ON CONFLICT(key) DO UPDATE SET value=excluded.value;
            """);

        var schemaVersion = await ScalarAsync(conn, "SELECT value FROM meta WHERE key='schema_version' LIMIT 1;");
        dbHealthy = string.Equals(schemaVersion?.Trim(), "1", StringComparison.Ordinal);
    }
    catch (Exception ex)
    {
        dbHealthy = false;
        dbError = ex.GetType().Name + ": " + ex.Message;
    }

    // GPU visibility evidence (best-effort)
    var gpuVisible = false;
    string? gpuEvidence = null;
    try
    {
        var psi = new ProcessStartInfo
        {
            FileName = "bash",
            Arguments = "-lc \"nvidia-smi --query-gpu=name,driver_version --format=csv,noheader\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        using var p = Process.Start(psi);
        if (p is not null && p.WaitForExit(1500))
        {
            var stdout = (p.StandardOutput.ReadToEnd() ?? "").Trim();
            gpuVisible = p.ExitCode == 0 && stdout.Length > 0;
            gpuEvidence = gpuVisible ? stdout : null;
        }
    }
    catch { }

    var payload = new
    {
        schema_version = 1,
        captured_utc = capturedUtc,
        core = new { reachable = true, base_url = "http://127.0.0.1:8484" },
        ollama = new { reachable = ollamaReachable, version = ollamaVersion, models_dir = ollamaModelsDir },
        pins = new { pinned_yaml_path = @"D:\Aetherforge\config\pinned.yaml", pins_match = (bool?)null, model_digests_match = (bool?)null },
        db = new { path = dbPath, healthy = dbHealthy, error = dbError },
        gpu = new { visible = gpuVisible, evidence = gpuEvidence },
        tailnet = new { serve_enabled = false, published_port = (int?)null },
        files = new { settings_exists = File.Exists(settingsPath), pinned_exists = File.Exists(pinnedPath) }
    };

    return Results.Json(payload);
});

app.Run();

static async Task ExecAsync(SqliteConnection conn, string sql)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    await cmd.ExecuteNonQueryAsync();
}

static async Task<string?> ScalarAsync(SqliteConnection conn, string sql)
{
    await using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    var obj = await cmd.ExecuteScalarAsync();
    return obj?.ToString();
}

sealed record OllamaVersion(string version);

