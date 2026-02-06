// D:/Aetherforge/src/Aetherforge.Core/Program.cs

using Aetherforge.Contracts;
using Microsoft.Data.Sqlite;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Aetherforge.Core;

public sealed class Program
{
    // Canonical loopback defaults (settings.yaml can override).
    private const string DefaultCoreBindUrl = "http://127.0.0.1:8484";
    private const string DefaultOllamaBaseUrl = "http://127.0.0.1:11434";

    // Default WSL-view paths (override via env if needed).
    private static readonly string SettingsPath = Env("AETHERFORGE_SETTINGS_PATH", "/mnt/d/Aetherforge/config/settings.yaml");
    private static readonly string PinnedPath = Env("AETHERFORGE_PINNED_PATH", "/mnt/d/Aetherforge/config/pinned.yaml");
    private static readonly string DbPath = Env("AETHERFORGE_DB_PATH", "/var/lib/aetherforge/conversations.sqlite");
    private static readonly string ExportsRootEnv = Env("AETHERFORGE_EXPORTS_ROOT", "/mnt/d/Aetherforge/exports");

    // Keep responses deterministic; attributes in Aetherforge.Contracts take precedence.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    // Reuse for short, best-effort probes (e.g., /v1/status). For streaming chat we use a dedicated client.
    private static readonly HttpClient StatusHttp = new() { Timeout = TimeSpan.FromSeconds(2) };

    public static async Task Main(string[] args)
    {
        // ---------------------- settings.yaml (load once) ----------------------
        var (settings, settingsErr) = SettingsLoader.TryLoad(SettingsPath);

        // Precedence:
        // - ports: settings.yaml > defaults
        // - exports root: env var (AETHERFORGE_EXPORTS_ROOT) > settings.yaml > default
        var coreBindUrl = settings?.Ports?.CoreBindUrl ?? DefaultCoreBindUrl;
        var ollamaBaseUrl = settings?.Ports?.OllamaBaseUrl ?? DefaultOllamaBaseUrl;

        var exportsRoot = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AETHERFORGE_EXPORTS_ROOT"))
            ? (settings?.Boundary?.Roots?.Wsl?.Exports ?? ExportsRootEnv)
            : ExportsRootEnv;

        var pinsPolicy = settings?.Pins ?? new PinsSettings();

        // ---------------------- pinned.yaml (load once) ----------------------
        var (pinned, pinnedErr) = PinnedLoader.TryLoad(PinnedPath);

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(coreBindUrl);

        // Ensure request binding uses the same serializer semantics as responses.
        builder.Services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.PropertyNamingPolicy = JsonOpts.PropertyNamingPolicy;
            o.SerializerOptions.DefaultIgnoreCondition = JsonOpts.DefaultIgnoreCondition;
            o.SerializerOptions.WriteIndented = JsonOpts.WriteIndented;
        });

        var app = builder.Build();

        // Init & config load.
        await Db.EnsureInitializedAsync(DbPath);

        var boundary = settings is not null
            ? BoundaryPolicy.FromSettings(settings, exportsRoot)
            : BoundaryPolicy.CreateDefault(SettingsPath, exportsRoot);

        // --------------------------- /v1/status ---------------------------
        app.MapGet("/v1/status", async () =>
        {
            var utc = UtcNowIso8601();

            // DB settings probe (best-effort).
            bool walMode = false;
            int busyTimeoutMs = 0;
            try
            {
                await using var conn = Db.Open(DbPath);
                await conn.OpenAsync();

                var journal = (await Db.ScalarAsync(conn, "PRAGMA journal_mode;"))?.Trim();
                walMode = string.Equals(journal, "wal", StringComparison.OrdinalIgnoreCase);

                var busy = (await Db.ScalarAsync(conn, "PRAGMA busy_timeout;"))?.Trim();
                if (!int.TryParse(busy, out busyTimeoutMs)) busyTimeoutMs = 0;
            }
            catch
            {
                walMode = false;
                busyTimeoutMs = 0;
            }

            // Ollama best-effort.
            bool ollamaReachable = false;
            string ollamaVersion = "unknown";
            OllamaTags? tags = null;

            try
            {
                var ver = await StatusHttp.GetFromJsonAsync<OllamaVersion>($"{ollamaBaseUrl}/api/version");
                if (ver is not null && !string.IsNullOrWhiteSpace(ver.Version))
                {
                    ollamaReachable = true;
                    ollamaVersion = ver.Version.Trim();
                    tags = await StatusHttp.GetFromJsonAsync<OllamaTags>($"{ollamaBaseUrl}/api/tags");
                }
            }
            catch { /* best-effort */ }

            // Pins verification (deterministic semantics).
            bool? pinsMatch = null;
            bool? modelDigestsMatch = null;
            string? pinsDetail = null;

            if (pinned is null)
            {
                pinsMatch = null;
                modelDigestsMatch = null;
                pinsDetail = pinnedErr ?? "pinned.yaml missing or unreadable";
            }
            else
            {
                var pinsValidation = PinnedValidator.Validate(pinned);
                pinsMatch = pinsValidation.IsValid;

                // Note: planned models may have digest=null when pins.mode=fallback. This is not fatal.
                pinsDetail = pinsValidation.Detail;

                if (tags?.Models is null)
                {
                    modelDigestsMatch = null;
                    pinsDetail = AppendDetail(pinsDetail, "ollama tags unavailable");
                }
                else
                {
                    modelDigestsMatch = PinnedVerifier.VerifyDigests(pinned, tags, out var verifyDetail);
                    pinsDetail = AppendDetail(pinsDetail, verifyDetail);
                }

                if (ollamaReachable && !string.IsNullOrWhiteSpace(pinned.Ollama.Version) &&
                    !string.Equals(pinned.Ollama.Version.Trim(), ollamaVersion, StringComparison.Ordinal))
                {
                    pinsDetail = AppendDetail(pinsDetail, $"ollama version mismatch (pinned={pinned.Ollama.Version}, live={ollamaVersion})");
                }

                // Surface settings validity without expanding the contract.
                if (settingsErr is not null)
                    pinsDetail = AppendDetail(pinsDetail, $"settings invalid: {settingsErr}");
            }

            var status = new StatusResponse(
                Utc: utc,
                Core: new StatusCoreInfo(
                    Reachable: true,
                    Version: CoreVersion(),
                    BaseUrl: coreBindUrl),
                Db: new StatusDbInfo(
                    Path: DbPath,
                    WalMode: walMode,
                    BusyTimeoutMs: busyTimeoutMs),
                Ollama: new StatusOllamaInfo(
                    Reachable: ollamaReachable,
                    Version: ollamaVersion,
                    BaseUrl: ollamaBaseUrl,
                    ModelsDir: null),
                Pins: new StatusPinsInfo(
                    PinsMatch: pinsMatch,
                    ModelDigestsMatch: modelDigestsMatch,
                    Detail: pinsDetail)
            );

            return Results.Json(status, options: JsonOpts);
        });

        // ---------------------- POST /v1/conversations ----------------------
        app.MapPost("/v1/conversations", async (ConversationCreateRequest req) =>
        {
            if (!Validation.TryRoleTier(req.Role, req.Tier, out var role, out var tier, out var err))
                return ApiError.BadRequest(err!);

            if (pinned is null)
                return ApiError.Failed("PIN_MISSING", "Pinned model map is missing", hint: pinnedErr ?? "Ensure config/pinned.yaml exists.");

            if (!PinsResolver.TryResolveModel(pinned, pinsPolicy, role, tier, out var resolved, out var resolution, out var resolveErr))
                return ApiError.BadRequest(resolveErr!);

            var title = (req.Title ?? "").Trim();
            if (title.Length == 0) title = $"{role}-{tier}";

            await using var conn = Db.Open(DbPath);
            await conn.OpenAsync();

            var createdUtc = UtcNowIso8601();
            var id = await Db.InsertConversationAsync(conn, createdUtc, role, tier, title, resolved.Tag, resolved.Digest);

            var dto = new ConversationDto(id, createdUtc, title, role, tier, resolved.Tag, resolved.Digest);

            // Persist resolution info (best-effort) in a synthetic meta message? Not yet.
            _ = resolution;

            return Results.Json(dto, options: JsonOpts);
        });

        // ---------------------- GET /v1/conversations/{id} ----------------------
        app.MapGet("/v1/conversations/{id:int}", async (int id) =>
        {
            await using var conn = Db.Open(DbPath);
            await conn.OpenAsync();

            var convo = await Db.GetConversationAsync(conn, id);
            if (convo is null) return ApiError.NotFound("NOT_FOUND", $"Conversation {id} not found");

            var msgs = await Db.GetMessagesAsync(conn, id);
            var dto = new ConversationWithMessagesDto(convo, msgs);
            return Results.Json(dto, options: JsonOpts);
        });

        // ---------------------- GET /v1/conversations?limit&offset&q ----------------------
        app.MapGet("/v1/conversations", async (int? limit, int? offset, string? q) =>
        {
            var lim = Math.Clamp(limit ?? 20, 1, 200);
            var off = Math.Max(offset ?? 0, 0);
            var query = (q ?? "").Trim();

            await using var conn = Db.Open(DbPath);
            await conn.OpenAsync();

            var items = await Db.ListConversationsAsync(conn, lim, off, query);
            var dto = new ConversationListResponse(items, lim, off, query.Length > 0 ? query : null);
            return Results.Json(dto, options: JsonOpts);
        });

        // ---------------------- PATCH /v1/conversations/{id} (title only) ----------------------
        app.MapPatch("/v1/conversations/{id:int}", async (int id, ConversationPatchRequest req) =>
        {
            var title = (req.Title ?? "").Trim();
            if (title.Length == 0) return ApiError.BadRequest(new ErrorResponse("BAD_REQUEST", "Title is required"));

            await using var conn = Db.Open(DbPath);
            await conn.OpenAsync();

            var updated = await Db.UpdateConversationTitleAsync(conn, id, title);
            if (!updated) return ApiError.NotFound("NOT_FOUND", $"Conversation {id} not found");

            var convo = await Db.GetConversationAsync(conn, id);
            return Results.Json(convo, options: JsonOpts);
        });

        // ---------------------- POST /v1/chat (SSE) ----------------------
        app.MapPost("/v1/chat", async (HttpContext http, ChatRequest req) =>
        {
            if (req.ConversationId <= 0) return ApiError.BadRequest(new ErrorResponse("BAD_REQUEST", "conversation_id is required"));
            if (string.IsNullOrWhiteSpace(req.Content)) return ApiError.BadRequest(new ErrorResponse("BAD_REQUEST", "content is required"));

            await using var conn = Db.Open(DbPath);
            await conn.OpenAsync();

            var convo = await Db.GetConversationAsync(conn, req.ConversationId);
            if (convo is null) return ApiError.NotFound("NOT_FOUND", $"Conversation {req.ConversationId} not found");

            // Boundary: SSE response headers.
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";
            http.Response.ContentType = "text/event-stream; charset=utf-8";

            // Persist user message.
            var nowUtc = UtcNowIso8601();
            _ = await Db.InsertMessageAsync(conn, req.ConversationId, nowUtc, "user", req.Content, metaJson: null);

            // Build Ollama chat request from DB history (simple replay). Important: fetch history
            // BEFORE inserting the assistant placeholder message, so the placeholder doesn't get
            // echoed back into the prompt.
            var history = await Db.GetMessagesAsync(conn, req.ConversationId);

            // Create assistant placeholder message to get message_id for SSE events.
            var assistantCreatedUtc = UtcNowIso8601();
            var assistantId = await Db.InsertMessageAsync(conn, req.ConversationId, assistantCreatedUtc, "assistant", content: "", metaJson: null);

            // Emit meta.
            var resolution = PinsResolver.TryExplainResolution(pinned, pinsPolicy, convo);
            await Sse.WriteEventAsync(
                http.Response,
                "meta",
                new SseMetaEvent(req.ConversationId, assistantId, convo.ModelTag, convo.ModelDigest, resolution),
                JsonOpts,
                http.RequestAborted);

            var ollamaReq = new OllamaChatRequest(
                convo.ModelTag,
                history.Select(m => new OllamaMessage(m.Sender == "assistant" ? "assistant" : "user", m.Content)).ToList(),
                Stream: true);

            var assistantBuf = new StringBuilder();

            try
            {
                using var httpClient = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

                using var msg = new HttpRequestMessage(HttpMethod.Post, $"{ollamaBaseUrl}/api/chat");
                msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-ndjson"));
                msg.Content = new StringContent(JsonSerializer.Serialize(ollamaReq, JsonOpts), Encoding.UTF8, "application/json");

                using var resp = await httpClient.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, http.RequestAborted);
                if (!resp.IsSuccessStatusCode)
                {
                    await Sse.WriteEventAsync(http.Response, "error", new ErrorResponse(
                        "OLLAMA_ERROR",
                        $"Ollama returned {(int)resp.StatusCode}",
                        Detail: await SafeReadAsync(resp),
                        Hint: "Verify Ollama is running and the model tag exists."
                    ),
                    JsonOpts,
                    http.RequestAborted);
                    return Results.Empty;
                }

                await using var stream = await resp.Content.ReadAsStreamAsync(http.RequestAborted);
                using var reader = new StreamReader(stream, Encoding.UTF8);

                // Avoid StreamReader.EndOfStream which can cause unintended sync blocking when no data
                // is buffered. ReadLineAsync returns null at EOF.
                while (!http.RequestAborted.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync();
                    if (line is null) break;
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var delta = OllamaNdjson.TryExtractDelta(line);
                    if (delta is null) continue;

                    assistantBuf.Append(delta);

                    await Sse.WriteEventAsync(
                        http.Response,
                        "delta",
                        new SseDeltaEvent(assistantId, delta),
                        JsonOpts,
                        http.RequestAborted);
                }

                // Persist assistant final content.
                await Db.UpdateMessageContentAsync(conn, assistantId, assistantBuf.ToString());

                await Sse.WriteEventAsync(
                    http.Response,
                    "done",
                    new SseDoneEvent(assistantId),
                    JsonOpts,
                    http.RequestAborted);

                return Results.Empty;
            }
            catch (OperationCanceledException)
            {
                // Cancellation: persist whatever we have and end silently.
                await Db.UpdateMessageContentAsync(conn, assistantId, assistantBuf.ToString());
                return Results.Empty;
            }
            catch (Exception ex)
            {
                await Db.UpdateMessageContentAsync(conn, assistantId, assistantBuf.ToString());

                await Sse.WriteEventAsync(http.Response, "error", new ErrorResponse(
                    "CHAT_FAILED",
                    "Chat failed",
                    Detail: $"{ex.GetType().Name}: {ex.Message}",
                    Hint: "Check Core logs and Ollama status."
                ),
                JsonOpts,
                CancellationToken.None);

                return Results.Empty;
            }
        });

        // ---------------------- POST /v1/export/{id} ----------------------
        app.MapPost("/v1/export/{id:int}", async (int id) =>
        {
            await using var conn = Db.Open(DbPath);
            await conn.OpenAsync();

            var convo = await Db.GetConversationAsync(conn, id);
            if (convo is null) return ApiError.NotFound("NOT_FOUND", $"Conversation {id} not found");

            var msgs = await Db.GetMessagesAsync(conn, id);

            // Resolve export dir: <exportsRoot>/YYYY-MM-DD (UTC).
            var dateDir = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var outDir = Path.Combine(exportsRoot, dateDir);

            // Boundary enforcement (allowlisted roots).
            if (!boundary.AllowWriteUnder(outDir, out var deny))
                return ApiError.Forbidden(deny!);

            Directory.CreateDirectory(outDir);

            var slug = Slug.Make(convo.Title);
            var mdPath = Path.Combine(outDir, $"{convo.Id}.{slug}.md");
            var jsonPath = Path.Combine(outDir, $"{convo.Id}.{slug}.json");

            // JSON export (schema v1).
            var export = new ConversationExportV1(
                SchemaVersion: 1,
                GeneratedUtc: UtcNowIso8601(),
                CoreVersion: CoreVersion(),
                Conversation: new ExportConversation(convo.Id, convo.CreatedUtc, convo.Title, convo.Role, convo.Tier),
                Model: new ExportModel(convo.ModelTag, convo.ModelDigest),
                Messages: msgs.Select(m => new ExportMessage(m.Id, m.CreatedUtc, m.Sender, m.Content, m.MetaJson)).ToList());

            var jsonText = JsonSerializer.Serialize(export, JsonOpts);
            await File.WriteAllTextAsync(jsonPath, jsonText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Markdown export.
            var md = new StringBuilder();
            md.AppendLine($"# Conversation {convo.Id}: {convo.Title}");
            md.AppendLine();
            md.AppendLine($"- created_utc: {convo.CreatedUtc}");
            md.AppendLine($"- role: {convo.Role}");
            md.AppendLine($"- tier: {convo.Tier}");
            md.AppendLine($"- model_tag: {convo.ModelTag}");
            md.AppendLine($"- model_digest: {convo.ModelDigest}");
            md.AppendLine();
            md.AppendLine("---");
            md.AppendLine();

            foreach (var m in msgs.OrderBy(x => x.Id))
            {
                md.AppendLine($"## {m.Sender} @ {m.CreatedUtc}");
                md.AppendLine();
                md.AppendLine(m.Content);
                md.AppendLine();
            }

            await File.WriteAllTextAsync(mdPath, md.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return Results.Json(new ExportResponse(true, outDir, mdPath, jsonPath), options: JsonOpts);
        });

        app.Run();
    }

    private static string Env(string name, string fallback)
    {
        var v = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(v) ? fallback : v;
    }

    private static string UtcNowIso8601() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    private static string CoreVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        return v is null ? "unknown" : v.ToString();
    }

    private static string? AppendDetail(string? existing, string? add)
    {
        add = (add ?? "").Trim();
        if (add.Length == 0) return existing;

        existing = (existing ?? "").Trim();
        if (existing.Length == 0) return add;

        return $"{existing}; {add}";
    }

    private static async Task<string?> SafeReadAsync(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync(); }
        catch { return null; }
    }
}

// --------------------------- Internal-to-Core responses ---------------------------

internal sealed record ExportResponse(
    [property: JsonPropertyName("ok")] bool Ok,
    [property: JsonPropertyName("dir")] string Dir,
    [property: JsonPropertyName("md")] string Md,
    [property: JsonPropertyName("json")] string Json
);

// Export schema v1
public sealed record ConversationExportV1(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("generated_utc")] string GeneratedUtc,
    [property: JsonPropertyName("core_version")] string CoreVersion,
    [property: JsonPropertyName("conversation")] ExportConversation Conversation,
    [property: JsonPropertyName("model")] ExportModel Model,
    [property: JsonPropertyName("messages")] List<ExportMessage> Messages
);

public sealed record ExportConversation(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("created_utc")] string CreatedUtc,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("tier")] string Tier
);

public sealed record ExportModel(
    [property: JsonPropertyName("tag")] string Tag,
    [property: JsonPropertyName("digest")] string Digest
);

public sealed record ExportMessage(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("created_utc")] string CreatedUtc,
    [property: JsonPropertyName("sender")] string Sender,
    [property: JsonPropertyName("content")] string Content,
    [property: JsonPropertyName("meta_json")] string? MetaJson
);

// --------------------------- Helpers ---------------------------

internal static class ApiError
{
    public static IResult BadRequest(ErrorResponse e) => Results.Json(e, statusCode: 400);
    public static IResult Forbidden(ErrorResponse e) => Results.Json(e, statusCode: 403);
    public static IResult NotFound(string code, string message) => Results.Json(new ErrorResponse(code, message), statusCode: 404);
    public static IResult Failed(string code, string message, string? detail = null, string? hint = null)
        => Results.Json(new ErrorResponse(code, message, detail, hint), statusCode: 500);
}

internal static class Validation
{
    public static bool TryRoleTier(string? roleRaw, string? tierRaw, out string role, out string tier, out ErrorResponse? err)
    {
        role = (roleRaw ?? "").Trim().ToLowerInvariant();
        tier = (tierRaw ?? "").Trim().ToLowerInvariant();
        err = null;

        if (role is not ("general" or "coding" or "agent"))
        {
            err = new ErrorResponse("BAD_REQUEST", $"Invalid role '{roleRaw}'");
            return false;
        }

        if (tier is not ("fast" or "thinking"))
        {
            err = new ErrorResponse("BAD_REQUEST", $"Invalid tier '{tierRaw}'");
            return false;
        }

        return true;
    }
}

internal sealed class BoundaryPolicy
{
    private readonly IReadOnlyList<string> _writeRoots;
    private readonly bool _blockReparsePoints;

    private BoundaryPolicy(IReadOnlyList<string> writeRoots, bool blockReparsePoints)
    {
        _writeRoots = writeRoots.Select(Path.GetFullPath).ToArray();
        _blockReparsePoints = blockReparsePoints;
    }

    public static BoundaryPolicy CreateDefault(string settingsPath, string exportsRoot)
    {
        _ = settingsPath; // reserved for future parsing
        return new BoundaryPolicy(new[] { exportsRoot }, blockReparsePoints: true);
    }

    public static BoundaryPolicy FromSettings(SettingsV1 settings, string fallbackExportsRoot)
    {
        var roots = (settings.Boundary.AllowWriteUnderWsl ?? new List<string>())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToList();

        if (roots.Count == 0)
            roots.Add(fallbackExportsRoot);

        return new BoundaryPolicy(roots, settings.Boundary.BlockReparsePoints);
    }

    public bool AllowWriteUnder(string candidateDir, out ErrorResponse? deny)
    {
        deny = null;

        var full = Path.GetFullPath(candidateDir);

        if (!_writeRoots.Any(r => IsDescendant(full, r)))
        {
            deny = new ErrorResponse(
                "BOUNDARY_DENY",
                "Write denied outside allowlisted roots",
                Detail: full,
                Hint: $"Allowed roots: {string.Join(", ", _writeRoots)}");
            return false;
        }

        // Best-effort reparse/symlink block on the *existing* path segments.
        if (_blockReparsePoints && HasReparsePointInPath(full))
        {
            deny = new ErrorResponse("BOUNDARY_DENY", "Write denied due to reparse point/symlink in path", Detail: full);
            return false;
        }

        return true;
    }

    private static bool IsDescendant(string path, string root)
    {
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasReparsePointInPath(string fullPath)
    {
        try
        {
            var parts = fullPath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                                .Where(p => !string.IsNullOrWhiteSpace(p))
                                .ToArray();

            // Rebuild progressively from root.
            string current = fullPath.StartsWith(Path.DirectorySeparatorChar)
                ? Path.DirectorySeparatorChar.ToString()
                : parts[0];

            for (int i = fullPath.StartsWith(Path.DirectorySeparatorChar) ? 0 : 1; i < parts.Length; i++)
            {
                current = Path.Combine(current, parts[i]);
                if (Directory.Exists(current))
                {
                    var attr = File.GetAttributes(current);
                    if ((attr & FileAttributes.ReparsePoint) != 0) return true;
                }
            }
        }
        catch { /* best-effort */ }

        return false;
    }
}

internal static class Slug
{
    public static string Make(string? title)
    {
        var s = (title ?? "").Trim().ToLowerInvariant();
        if (s.Length == 0) s = "conversation";

        var sb = new StringBuilder(s.Length);
        bool dash = false;

        foreach (var ch in s)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(ch);
                dash = false;
            }
            else
            {
                if (!dash)
                {
                    sb.Append('-');
                    dash = true;
                }
            }
        }

        var outp = sb.ToString().Trim('-');
        if (outp.Length == 0) outp = "conversation";
        if (outp.Length > 64) outp = outp[..64].Trim('-');
        return outp;
    }
}

internal static class Sse
{
    public static async Task WriteEventAsync(HttpResponse resp, string eventName, object payload, JsonSerializerOptions jsonOptions, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, jsonOptions);
        await resp.WriteAsync($"event: {eventName}\n", ct);
        await resp.WriteAsync($"data: {json}\n\n", ct);
        await resp.Body.FlushAsync(ct);
    }
}


// --------------------------- pinned.yaml (YAML deserialize) ---------------------------

internal sealed record PinnedV1
{
    public int SchemaVersion { get; init; }
    public string? CapturedUtc { get; init; }
    public PinnedOllama Ollama { get; init; } = new();

    // role -> tier -> entry
    public Dictionary<string, Dictionary<string, PinnedModel>> Models { get; init; } = new();
}

internal sealed record PinnedOllama
{
    public string? Version { get; init; }
}

internal sealed record PinnedModel
{
    public string? Tag { get; init; }
    public string? Digest { get; init; }
    public bool Required { get; init; }
}

internal static class PinnedLoader
{
    public static (PinnedV1? Pinned, string? Error) TryLoad(string pinnedPath)
    {
        if (!File.Exists(pinnedPath))
            return (null, $"pinned.yaml missing: {pinnedPath}");

        var yaml = File.ReadAllText(pinnedPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(yaml))
            return (null, $"pinned.yaml is empty: {pinnedPath}");

        try
        {
            var deserializer = new DeserializerBuilder()
                .WithNamingConvention(UnderscoredNamingConvention.Instance) // snake_case <-> PascalCase
                .IgnoreUnmatchedProperties()
                .Build();

            var pinned = deserializer.Deserialize<PinnedV1>(yaml);
            if (pinned is null)
                return (null, "pinned.yaml deserialized to null");

            if (pinned.SchemaVersion != 1)
                return (null, $"pinned.yaml schema_version must be 1 (got {pinned.SchemaVersion})");

            // Normalize keys and digests for consistent lookups.
            pinned = pinned with { Models = NormalizeModels(pinned.Models) };

            return (pinned, null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Dictionary<string, Dictionary<string, PinnedModel>> NormalizeModels(Dictionary<string, Dictionary<string, PinnedModel>>? models)
    {
        var outp = new Dictionary<string, Dictionary<string, PinnedModel>>(StringComparer.OrdinalIgnoreCase);
        if (models is null) return outp;

        foreach (var (roleRaw, tiersRaw) in models)
        {
            var role = (roleRaw ?? "").Trim().ToLowerInvariant();
            if (role.Length == 0) continue;

            var tiers = new Dictionary<string, PinnedModel>(StringComparer.OrdinalIgnoreCase);
            if (tiersRaw is not null)
            {
                foreach (var (tierRaw, entryRaw) in tiersRaw)
                {
                    var tier = (tierRaw ?? "").Trim().ToLowerInvariant();
                    if (tier.Length == 0) continue;

                    var tag = (entryRaw?.Tag ?? "").Trim();
                    var dig = NormalizeDigest(entryRaw?.Digest);

                    tiers[tier] = (entryRaw ?? new PinnedModel()) with
                    {
                        Tag = tag.Length == 0 ? null : tag,
                        Digest = dig
                    };
                }
            }

            outp[role] = tiers;
        }

        return outp;
    }

    private static string? NormalizeDigest(string? s)
    {
        s = (s ?? "").Trim().Trim('"').ToLowerInvariant();
        if (s.Length == 0) return null;
        if (string.Equals(s, "null", StringComparison.OrdinalIgnoreCase)) return null;

        if (s.StartsWith("sha256:", StringComparison.Ordinal))
            s = s["sha256:".Length..];

        return IsDigest64(s) ? s : null;
    }

    private static bool IsDigest64(string s)
        => s.Length == 64 && s.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
}

internal static class PinnedValidator
{
    public sealed record Result(bool IsValid, string? Detail);

    public static Result Validate(PinnedV1 pinned)
    {
        if (pinned.SchemaVersion != 1)
            return new Result(false, $"pinned schema_version must be 1 (got {pinned.SchemaVersion})");

        if (pinned.Models is null || pinned.Models.Count == 0)
            return new Result(false, "pinned models map is empty");

        var missing = new List<string>();

        foreach (var (role, tiers) in pinned.Models)
        {
            foreach (var (tier, entry) in tiers)
            {
                if (entry is null) continue;

                if (entry.Required)
                {
                    if (string.IsNullOrWhiteSpace(entry.Tag) || string.IsNullOrWhiteSpace(entry.Digest))
                        missing.Add($"{role}.{tier}");
                }
            }
        }

        return missing.Count == 0
            ? new Result(true, null)
            : new Result(false, $"missing required pins: {string.Join(", ", missing)}");
    }
}

internal static class PinnedVerifier
{
    public static bool VerifyDigests(PinnedV1 pinned, OllamaTags tags, out string? detail)
    {
        detail = null;

        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in tags.Models)
        {
            if (string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Digest)) continue;
            live[m.Name] = NormalizeDigest(m.Digest)!;
        }

        var mismatches = new List<string>();

        foreach (var (role, tiers) in pinned.Models)
        {
            foreach (var (tier, entry) in tiers)
            {
                var tag = (entry.Tag ?? "").Trim();
                var dig = NormalizeDigest(entry.Digest);

                if (tag.Length == 0) continue;
                if (dig is null) continue; // planned/not pinned yet

                if (!live.TryGetValue(tag, out var got))
                {
                    mismatches.Add($"{role}.{tier}: missing model tag {tag}");
                    continue;
                }

                if (!string.Equals(got, dig, StringComparison.Ordinal))
                    mismatches.Add($"{role}.{tier}: digest mismatch for {tag}");
            }
        }

        if (mismatches.Count == 0) return true;

        detail = string.Join("; ", mismatches);
        return false;
    }

    private static string? NormalizeDigest(string? s)
    {
        s = (s ?? "").Trim().Trim('"').ToLowerInvariant();
        if (s.Length == 0) return null;

        if (s.StartsWith("sha256:", StringComparison.Ordinal))
            s = s["sha256:".Length..];

        return s;
    }
}

internal sealed record ResolvedModel(string Tag, string Digest);

internal static class PinsResolver
{
    public static bool TryResolveModel(PinnedV1 pinned, PinsSettings policy, string role, string tier, out ResolvedModel model, out string? resolution, out ErrorResponse? err)
    {
        model = new ResolvedModel("", "");
        resolution = null;
        err = null;

        var mode = (policy.Mode ?? "strict").Trim().ToLowerInvariant();
        var fbRole = (policy.FallbackRole ?? "general").Trim().ToLowerInvariant();
        var fbTier = (policy.FallbackTier ?? "fast").Trim().ToLowerInvariant();

        bool strict = mode != "fallback";

        // 1) Try direct.
        if (TryGetPinned(pinned, role, tier, out var direct))
        {
            model = direct;
            return true;
        }

        if (strict)
        {
            err = new ErrorResponse(
                "PIN_NO_MATCH",
                $"No usable pinned model for role={role} tier={tier}",
                Detail: "Pin is missing or has digest=null",
                Hint: "Update pinned.yaml or switch pins.mode to fallback in settings.yaml.");
            return false;
        }

        // 2) Fallback.
        if (TryGetPinned(pinned, fbRole, fbTier, out var fb))
        {
            model = fb;
            resolution = $"fallback:{fbRole}.{fbTier}";
            return true;
        }

        err = new ErrorResponse(
            "PIN_NO_MATCH",
            $"No usable pinned model for role={role} tier={tier} and fallback={fbRole}.{fbTier}",
            Detail: "Pin is missing or has digest=null",
            Hint: "Ensure fallback_role/fallback_tier points to a pinned model with a non-null digest.");
        return false;
    }

    public static string? TryExplainResolution(PinnedV1? pinned, PinsSettings policy, ConversationDto convo)
    {
        if (pinned is null) return null;

        if (!TryResolveModel(pinned, policy, convo.Role, convo.Tier, out var resolved, out var resolution, out _))
            return null;

        // If the current policy would resolve to the same model, treat as no special resolution.
        if (string.Equals(resolved.Tag, convo.ModelTag, StringComparison.Ordinal) &&
            string.Equals(resolved.Digest, convo.ModelDigest, StringComparison.Ordinal))
        {
            return null;
        }

        return resolution;
    }

    private static bool TryGetPinned(PinnedV1 pinned, string role, string tier, out ResolvedModel model)
    {
        model = new ResolvedModel("", "");

        if (!pinned.Models.TryGetValue(role, out var tiers) || tiers is null)
            return false;

        if (!tiers.TryGetValue(tier, out var entry) || entry is null)
            return false;

        var tag = (entry.Tag ?? "").Trim();
        var dig = (entry.Digest ?? "").Trim().ToLowerInvariant();

        if (tag.Length == 0) return false;
        if (dig.Length != 64) return false;

        model = new ResolvedModel(tag, dig);
        return true;
    }
}

// --------------------------- Ollama NDJSON parsing ---------------------------

internal static class OllamaNdjson
{
    public static string? TryExtractDelta(string jsonLine)
    {
        try
        {
            using var doc = JsonDocument.Parse(jsonLine);
            if (doc.RootElement.TryGetProperty("message", out var msg) &&
                msg.ValueKind == JsonValueKind.Object &&
                msg.TryGetProperty("content", out var content) &&
                content.ValueKind == JsonValueKind.String)
            {
                var s = content.GetString();
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }
        catch { /* ignore */ }

        return null;
    }
}

// Ollama request/response DTOs
internal sealed record OllamaVersion([property: JsonPropertyName("version")] string Version);

internal sealed record OllamaChatRequest(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("messages")] List<OllamaMessage> Messages,
    [property: JsonPropertyName("stream")] bool Stream);

internal sealed record OllamaMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

// --------------------------- DB ---------------------------

internal static class Db
{
    public static SqliteConnection Open(string dbPath)
    {
        var csb = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        };

        return new SqliteConnection(csb.ToString());
    }

    public static async Task EnsureInitializedAsync(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);

        await using var conn = Open(dbPath);
        await conn.OpenAsync();

        await ExecAsync(conn, "PRAGMA journal_mode=WAL;");
        await ExecAsync(conn, "PRAGMA synchronous=NORMAL;");
        await ExecAsync(conn, "PRAGMA foreign_keys=ON;");
        await ExecAsync(conn, "PRAGMA busy_timeout=5000;");

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS meta (
              key   TEXT PRIMARY KEY,
              value TEXT NOT NULL
            );
            """);

        await ExecAsync(conn, """
            INSERT INTO meta(key,value) VALUES('schema_version','1')
              ON CONFLICT(key) DO NOTHING;
            """);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS conversations (
              id           INTEGER PRIMARY KEY AUTOINCREMENT,
              created_utc   TEXT NOT NULL,
              title        TEXT NOT NULL,
              role         TEXT NOT NULL,
              tier         TEXT NOT NULL,
              model_tag    TEXT NOT NULL,
              model_digest TEXT NOT NULL
            );
            """);

        await ExecAsync(conn, """
            CREATE TABLE IF NOT EXISTS messages (
              id              INTEGER PRIMARY KEY AUTOINCREMENT,
              conversation_id INTEGER NOT NULL,
              created_utc      TEXT NOT NULL,
              sender          TEXT NOT NULL,
              content         TEXT NOT NULL,
              meta_json       TEXT NULL,
              FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
            );
            """);

        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_messages_convo ON messages(conversation_id, id);");
        await ExecAsync(conn, "CREATE INDEX IF NOT EXISTS idx_conversations_created ON conversations(created_utc DESC);");
    }

    public static async Task<int> InsertConversationAsync(SqliteConnection conn, string createdUtc, string role, string tier, string title, string tag, string digest)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO conversations(created_utc,title,role,tier,model_tag,model_digest)
            VALUES($created,$title,$role,$tier,$tag,$digest);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$created", createdUtc);
        cmd.Parameters.AddWithValue("$title", title);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$tier", tier);
        cmd.Parameters.AddWithValue("$tag", tag);
        cmd.Parameters.AddWithValue("$digest", digest);

        var obj = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(obj);
    }

    public static async Task<int> InsertMessageAsync(SqliteConnection conn, int conversationId, string createdUtc, string sender, string content, string? metaJson)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO messages(conversation_id,created_utc,sender,content,meta_json)
            VALUES($cid,$created,$sender,$content,$meta);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$cid", conversationId);
        cmd.Parameters.AddWithValue("$created", createdUtc);
        cmd.Parameters.AddWithValue("$sender", sender);
        cmd.Parameters.AddWithValue("$content", content);
        cmd.Parameters.AddWithValue("$meta", (object?)metaJson ?? DBNull.Value);

        var obj = await cmd.ExecuteScalarAsync();
        return Convert.ToInt32(obj);
    }

    public static async Task UpdateMessageContentAsync(SqliteConnection conn, int messageId, string content)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE messages SET content=$c WHERE id=$id;";
        cmd.Parameters.AddWithValue("$c", content);
        cmd.Parameters.AddWithValue("$id", messageId);
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<ConversationDto?> GetConversationAsync(SqliteConnection conn, int id)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id,created_utc,title,role,tier,model_tag,model_digest
            FROM conversations
            WHERE id=$id
            LIMIT 1;
            """;
        cmd.Parameters.AddWithValue("$id", id);

        await using var r = await cmd.ExecuteReaderAsync();
        if (!await r.ReadAsync()) return null;

        return new ConversationDto(
            Id: r.GetInt32(0),
            CreatedUtc: r.GetString(1),
            Title: r.GetString(2),
            Role: r.GetString(3),
            Tier: r.GetString(4),
            ModelTag: r.GetString(5),
            ModelDigest: r.GetString(6)
        );
    }

    public static async Task<List<MessageDto>> GetMessagesAsync(SqliteConnection conn, int conversationId)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id,created_utc,sender,content,meta_json
            FROM messages
            WHERE conversation_id=$cid
            ORDER BY id ASC;
            """;
        cmd.Parameters.AddWithValue("$cid", conversationId);

        var list = new List<MessageDto>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new MessageDto(
                Id: r.GetInt32(0),
                CreatedUtc: r.GetString(1),
                Sender: r.GetString(2),
                Content: r.GetString(3),
                MetaJson: r.IsDBNull(4) ? null : r.GetString(4)
            ));
        }
        return list;
    }

    public static async Task<List<ConversationDto>> ListConversationsAsync(SqliteConnection conn, int limit, int offset, string q)
    {
        var hasQ = !string.IsNullOrWhiteSpace(q);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = hasQ
            ? """
              SELECT id,created_utc,title,role,tier,model_tag,model_digest
              FROM conversations
              WHERE title LIKE $q
              ORDER BY id DESC
              LIMIT $limit OFFSET $offset;
              """
            : """
              SELECT id,created_utc,title,role,tier,model_tag,model_digest
              FROM conversations
              ORDER BY id DESC
              LIMIT $limit OFFSET $offset;
              """;

        if (hasQ) cmd.Parameters.AddWithValue("$q", $"%{q}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        cmd.Parameters.AddWithValue("$offset", offset);

        var list = new List<ConversationDto>();
        await using var r = await cmd.ExecuteReaderAsync();
        while (await r.ReadAsync())
        {
            list.Add(new ConversationDto(
                Id: r.GetInt32(0),
                CreatedUtc: r.GetString(1),
                Title: r.GetString(2),
                Role: r.GetString(3),
                Tier: r.GetString(4),
                ModelTag: r.GetString(5),
                ModelDigest: r.GetString(6)
            ));
        }

        return list;
    }

    public static async Task<bool> UpdateConversationTitleAsync(SqliteConnection conn, int id, string title)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE conversations SET title=$t WHERE id=$id;";
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$id", id);
        var n = await cmd.ExecuteNonQueryAsync();
        return n > 0;
    }

    public static async Task ExecAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }

    public static async Task<string?> ScalarAsync(SqliteConnection conn, string sql)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        var obj = await cmd.ExecuteScalarAsync();
        return obj?.ToString();
    }
}
