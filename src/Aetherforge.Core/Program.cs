using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Aetherforge.Contracts; // Import canonical API contracts
using System.Text.Json.Serialization;

namespace Aetherforge.Core;

public sealed class Program
{
    // Canonical loopback (spec)
    private const string CoreBindUrl = "http://127.0.0.1:8484";
    private const string OllamaBaseUrl = "http://127.0.0.1:11434";

    // Default WSL-view paths (override via env if needed)
    private static readonly string SettingsPath = Env("AETHERFORGE_SETTINGS_PATH", "/mnt/d/Aetherforge/config/settings.yaml");
    private static readonly string PinnedPath = Env("AETHERFORGE_PINNED_PATH", "/mnt/d/Aetherforge/config/pinned.yaml");
    private static readonly string DbPath = Env("AETHERFORGE_DB_PATH", "/var/lib/aetherforge/conversations.sqlite");
    private static readonly string ExportsRoot = Env("AETHERFORGE_EXPORTS_ROOT", "/mnt/d/Aetherforge/exports");

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
        // Requires SettingsLoader.cs (YamlDotNet + UnderscoredNamingConvention) to exist in Aetherforge.Core.
        var (settings, settingsErr) = SettingsLoader.TryLoad(SettingsPath);

        // Precedence:
        // - ports: settings.yaml > constants
        // - exports root: env var (AETHERFORGE_EXPORTS_ROOT) > settings.yaml > default
        var coreBindUrl = settings?.Ports?.CoreBindUrl ?? CoreBindUrl;
        var ollamaBaseUrl = settings?.Ports?.OllamaBaseUrl ?? OllamaBaseUrl;

        var exportsRoot = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AETHERFORGE_EXPORTS_ROOT"))
            ? (settings?.Boundary?.Roots?.Wsl?.Exports ?? ExportsRoot)
            : ExportsRoot;

        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.UseUrls(coreBindUrl);

        var app = builder.Build();

        // Init & config load
        await Db.EnsureInitializedAsync(DbPath);

        var pins = Pins.TryLoadPinned(PinnedPath);

        var policy = settings is not null
            ? BoundaryPolicy.FromSettings(settings, exportsRoot)
            : BoundaryPolicy.CreateDefault(SettingsPath, exportsRoot);

        // --------------------------- /v1/status ---------------------------
        app.MapGet("/v1/status", async () =>
        {
            var capturedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            // Ollama best-effort
            bool ollamaReachable = false;
            string? ollamaVersion = null;
            OllamaTags? tags = null;

            try
            {
                var ver = await StatusHttp.GetFromJsonAsync<OllamaVersion>($"{ollamaBaseUrl}/api/version");
                ollamaReachable = ver is not null && !string.IsNullOrWhiteSpace(ver.Version);
                ollamaVersion = ver?.Version;
                if (ollamaReachable)
                    tags = await StatusHttp.GetFromJsonAsync<OllamaTags>($"{ollamaBaseUrl}/api/tags");
            }
            catch { /* best-effort */ }

            // Pins verification (deterministic semantics)
            bool? pinsMatch = null;
            bool? modelDigestsMatch = null;
            string? pinsDetail = null;

            if (pins is null)
            {
                pinsMatch = null;
                modelDigestsMatch = null;
                pinsDetail = "pinned.yaml missing or unreadable";
            }
            else if (tags?.Models is null)
            {
                pinsMatch = null;
                modelDigestsMatch = null;
                pinsDetail = "ollama tags unavailable";
            }
            else
            {
                pinsMatch = true;
                modelDigestsMatch = Pins.VerifyAgainstTags(pins, tags);
            }

            // DB health
            var dbHealthy = false;
            string? dbError = null;
            try
            {
                await using var conn = Db.Open(DbPath);
                await conn.OpenAsync();
                var v = await Db.ScalarAsync(conn, "SELECT value FROM meta WHERE key='schema_version' LIMIT 1;");
                dbHealthy = v?.Trim() == "1";
            }
            catch (Exception ex)
            {
                dbHealthy = false;
                dbError = $"{ex.GetType().Name}: {ex.Message}";
            }

            // GPU evidence (best-effort)
            bool gpuVisible = false;
            string? gpuEvidence = null;
            try
            {
                var (ok, outp) = Proc.TryRun("bash", "-lc \"nvidia-smi --query-gpu=name,driver_version --format=csv,noheader\"");
                gpuVisible = ok && !string.IsNullOrWhiteSpace(outp);
                gpuEvidence = gpuVisible ? outp.Trim() : null;
            }
            catch { /* best-effort */ }

            // Settings validity (surface loader/validator result)
            bool? settingsValid =
                settingsErr is null
                    ? (settings is not null ? true : (bool?)null)
                    : false;

            var status = new StatusResponse(
                1,
                capturedUtc,
                new StatusCoreInfo(true, coreBindUrl),
                new StatusOllamaInfo(ollamaReachable, ollamaVersion),
                new StatusPinsInfo(PinnedPath, pinsMatch, modelDigestsMatch, pinsDetail),
                new StatusDbInfo(DbPath, dbHealthy, dbError),
                new StatusGpuInfo(gpuVisible, gpuEvidence),
                new StatusTailnetInfo(false, null),
                new StatusFilesInfo(
                    File.Exists(SettingsPath),
                    File.Exists(PinnedPath),
                    settingsValid,
                    settingsErr
                )
            );

            return Results.Json(status, options: JsonOpts);
        });

        // ---------------------- POST /v1/conversations ----------------------
        app.MapPost("/v1/conversations", async (ConversationCreateRequest req) =>
        {
            if (!Validation.TryRoleTier(req.Role, req.Tier, out var role, out var tier, out var err))
                return ApiError.BadRequest(err!);

            if (pins is null)
                return ApiError.Failed("PIN_MISSING", "Pinned model map is missing", hint: "Ensure config/pinned.yaml exists.");

            if (!pins.TryGetValue((role, tier), out var model))
                return ApiError.BadRequest(new ErrorResponse(
                    "PIN_NO_MATCH",
                    $"No pinned model for role={role} tier={tier}",
                    null,
                    "Update pinned.yaml model mapping."
                ));

            var title = (req.Title ?? "").Trim();
            if (title.Length == 0) title = $"{role}-{tier}";

            await using var conn = Db.Open(DbPath);
            await conn.OpenAsync();

            var createdUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var id = await Db.InsertConversationAsync(conn, createdUtc, role, tier, title, model.Tag, model.Digest);

            var dto = new ConversationDto(id, createdUtc, title, role, tier, model.Tag, model.Digest);
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

            // Boundary: SSE response headers
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.Connection = "keep-alive";
            http.Response.ContentType = "text/event-stream; charset=utf-8";

            // Persist user message
            var nowUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            _ = await Db.InsertMessageAsync(conn, req.ConversationId, nowUtc, "user", req.Content, metaJson: null);

            // Build Ollama chat request from DB history (simple replay). Important: fetch history
            // BEFORE inserting the assistant placeholder message, so the placeholder doesn't get
            // echoed back into the prompt.
            var history = await Db.GetMessagesAsync(conn, req.ConversationId);

            // Create assistant placeholder message to get message_id for SSE events
            var assistantCreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var assistantId = await Db.InsertMessageAsync(conn, req.ConversationId, assistantCreatedUtc, "assistant", content: "", metaJson: null);

            // Emit meta
            await Sse.WriteEventAsync(
                http.Response,
                "meta",
                new SseMetaEvent(req.ConversationId, assistantId, convo.ModelTag, convo.ModelDigest),
                JsonOpts,
                http.RequestAborted);

            var ollamaReq = new OllamaChatRequest(
                convo.ModelTag,
                history.Select(m => new OllamaMessage(m.Sender == "assistant" ? "assistant" : "user", m.Content)).ToList(),
                true);

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

                // Persist assistant final content
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
                // Cancellation: persist whatever we have and end silently
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

            // Resolve export dir: ExportsRoot/YYYY-MM-DD (UTC)
            var dateDir = DateTime.UtcNow.ToString("yyyy-MM-dd");
            var outDir = Path.Combine(exportsRoot, dateDir);

            // Boundary enforcement (allowlisted roots)
            if (!policy.AllowWriteUnder(outDir, out var deny))
                return ApiError.Forbidden(deny!);

            Directory.CreateDirectory(outDir);

            var slug = Slug.Make(convo.Title);
            var mdPath = Path.Combine(outDir, $"{convo.Id}.{slug}.md");
            var jsonPath = Path.Combine(outDir, $"{convo.Id}.{slug}.json");

            // JSON export (schema v1)
            var export = new ConversationExportV1(
                1,
                DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                "unknown",
                new ExportConversation(convo.Id, convo.CreatedUtc, convo.Title, convo.Role, convo.Tier),
                new ExportModel(convo.ModelTag, convo.ModelDigest),
                msgs.Select(m => new ExportMessage(m.Id, m.CreatedUtc, m.Sender, m.Content, m.MetaJson)).ToList());

            var jsonText = JsonSerializer.Serialize(export, JsonOpts);
            await File.WriteAllTextAsync(jsonPath, jsonText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            // Markdown export
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

    private static async Task<string?> SafeReadAsync(HttpResponseMessage resp)
    {
        try { return await resp.Content.ReadAsStringAsync(); }
        catch { return null; }
    }
}

// --------------------------- DTOs ---------------------------
// The API request/response DTOs are defined in the Aetherforge.Contracts project.
// We intentionally omit them here to avoid duplication. See Contracts.cs for:
//   ConversationCreateRequest
//   ConversationPatchRequest
//   ChatRequest
//   ConversationDto
//   MessageDto
//   ConversationWithMessagesDto
//   ConversationListResponse

// --------------------------- Responses internal to Core ---------------------------

internal sealed record StatusResponse(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("captured_utc")] string CapturedUtc,
    [property: JsonPropertyName("core")] StatusCoreInfo Core,
    [property: JsonPropertyName("ollama")] StatusOllamaInfo Ollama,
    [property: JsonPropertyName("pins")] StatusPinsInfo Pins,
    [property: JsonPropertyName("db")] StatusDbInfo Db,
    [property: JsonPropertyName("gpu")] StatusGpuInfo Gpu,
    [property: JsonPropertyName("tailnet")] StatusTailnetInfo Tailnet,
    [property: JsonPropertyName("files")] StatusFilesInfo Files
);

internal sealed record StatusCoreInfo(
    [property: JsonPropertyName("reachable")] bool Reachable,
    [property: JsonPropertyName("base_url")] string BaseUrl
);

internal sealed record StatusOllamaInfo(
    [property: JsonPropertyName("reachable")] bool Reachable,
    [property: JsonPropertyName("version")] string? Version
);

internal sealed record StatusPinsInfo(
    [property: JsonPropertyName("pinned_yaml_path")] string PinnedYamlPath,
    [property: JsonPropertyName("pins_match")] bool? PinsMatch,
    [property: JsonPropertyName("model_digests_match")] bool? ModelDigestsMatch,
    [property: JsonPropertyName("detail")] string? Detail
);

internal sealed record StatusDbInfo(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("healthy")] bool Healthy,
    [property: JsonPropertyName("error")] string? Error
);

internal sealed record StatusGpuInfo(
    [property: JsonPropertyName("visible")] bool Visible,
    [property: JsonPropertyName("evidence")] string? Evidence
);

internal sealed record StatusTailnetInfo(
    [property: JsonPropertyName("serve_enabled")] bool ServeEnabled,
    [property: JsonPropertyName("published_port")] int? PublishedPort
);

internal sealed record StatusFilesInfo(
    [property: JsonPropertyName("settings_exists")] bool SettingsExists,
    [property: JsonPropertyName("pinned_exists")] bool PinnedExists,
    [property: JsonPropertyName("settings_valid")] bool? SettingsValid,
    [property: JsonPropertyName("settings_error")] string? SettingsError
);

internal sealed record SseMetaEvent(
    [property: JsonPropertyName("conversation_id")] int ConversationId,
    [property: JsonPropertyName("message_id")] int MessageId,
    [property: JsonPropertyName("model_tag")] string ModelTag,
    [property: JsonPropertyName("model_digest")] string ModelDigest
);

internal sealed record SseDeltaEvent(
    [property: JsonPropertyName("message_id")] int MessageId,
    [property: JsonPropertyName("delta_text")] string DeltaText
);

internal sealed record SseDoneEvent(
    [property: JsonPropertyName("message_id")] int MessageId
);

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
    public static bool TryRoleTier(string roleRaw, string tierRaw, out string role, out string tier, out ErrorResponse? err)
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

        // Best-effort reparse/symlink block on the *existing* path segments
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

            // Rebuild progressively from root
            string current = fullPath.StartsWith(Path.DirectorySeparatorChar) ? Path.DirectorySeparatorChar.ToString() : parts[0];

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

internal static class Proc
{
    public static (bool ok, string output) TryRun(string file, string args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = file,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var p = Process.Start(psi);
        if (p is null) return (false, "");

        // Avoid potential hangs when reading redirected output from a long-running process.
        if (!p.WaitForExit(1500))
        {
            try { p.Kill(entireProcessTree: true); } catch { /* best-effort */ }
        }

        // With the process exited (or killed), these reads won't block indefinitely.
        var outp = (p.StandardOutput.ReadToEnd() ?? "");
        return (p.ExitCode == 0, outp);
    }
}

// --------------------------- Pins (minimal YAML parse) ---------------------------

internal sealed record ModelInfo(string Tag, string Digest);

internal static class Pins
{
    public static Dictionary<(string role, string tier), ModelInfo>? TryLoadPinned(string pinnedPath)
    {
        if (!File.Exists(pinnedPath)) return null;

        string? currentRole = null;
        string? currentTier = null;

        var map = new Dictionary<(string, string), ModelInfo>();

        foreach (var raw in File.ReadLines(pinnedPath))
        {
            var line = raw.TrimEnd();
            if (string.IsNullOrWhiteSpace(line)) continue;
            var t = line.TrimStart();
            if (t.StartsWith('#')) continue;

            var indent = line.Length - t.Length;

            // role key under models (indent 2)
            if (indent == 2 && t.EndsWith(':') && t != "ollama:" && t != "models:")
            {
                currentRole = t[..^1].Trim().ToLowerInvariant();
                currentTier = null;
                continue;
            }

            // tier key under role (indent 4)
            if (indent == 4 && t.EndsWith(':'))
            {
                currentTier = t[..^1].Trim().ToLowerInvariant();
                continue;
            }

            // tag/digest under tier (indent >=6)
            if (indent >= 6 && currentRole is not null && currentTier is not null)
            {
                if (t.StartsWith("tag:", StringComparison.Ordinal))
                {
                    var tag = Scalar(t["tag:".Length..]);
                    if (!map.TryGetValue((currentRole, currentTier), out var existing))
                        map[(currentRole, currentTier)] = new ModelInfo(tag, "");
                    else
                        map[(currentRole, currentTier)] = existing with { Tag = tag };
                }
                else if (t.StartsWith("digest:", StringComparison.Ordinal))
                {
                    var dig = NormalizeDigest(Scalar(t["digest:".Length..]));
                    if (!IsDigest64(dig)) continue;

                    if (!map.TryGetValue((currentRole, currentTier), out var existing))
                        map[(currentRole, currentTier)] = new ModelInfo("", dig);
                    else
                        map[(currentRole, currentTier)] = existing with { Digest = dig };
                }
            }
        }

        // Drop incomplete entries
        var pruned = map.Where(kv => !string.IsNullOrWhiteSpace(kv.Value.Tag) && !string.IsNullOrWhiteSpace(kv.Value.Digest))
                        .ToDictionary(k => k.Key, v => v.Value);

        return pruned.Count == 0 ? null : pruned;
    }

    public static bool VerifyAgainstTags(Dictionary<(string role, string tier), ModelInfo> pinned, OllamaTags tags)
    {
        var live = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var m in tags.Models)
        {
            if (string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Digest)) continue;
            live[m.Name] = NormalizeDigest(m.Digest);
        }

        foreach (var kv in pinned.Values)
        {
            if (!live.TryGetValue(kv.Tag, out var got)) return false;
            if (!string.Equals(got, kv.Digest, StringComparison.Ordinal)) return false;
        }

        return true;
    }

    private static string Scalar(string s)
    {
        s = s.Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
        return s.Trim();
    }

    private static string NormalizeDigest(string s)
    {
        s = s.Trim().Trim('"').ToLowerInvariant();
        return s.StartsWith("sha256:", StringComparison.Ordinal) ? s["sha256:".Length..] : s;
    }

    private static bool IsDigest64(string s)
        => s.Length == 64 && s.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
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

internal sealed record OllamaTags([property: JsonPropertyName("models")] List<OllamaModel> Models);

internal sealed record OllamaModel(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("digest")] string? Digest
);

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
