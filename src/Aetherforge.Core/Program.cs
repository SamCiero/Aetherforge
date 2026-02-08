// D:/Aetherforge/src/Aetherforge.Core/Program.cs

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

    // /v1/status must keep stable keys even when values are null (spec 7.4.1).
    private static readonly JsonSerializerOptions StatusJsonOpts = new(JsonOpts)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
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

        // Load pins manifest and capture any validation error. Unlike the previous
        // dictionary-based loader, TryLoadPinned now returns a manifest and a
        // human‑readable error. Preserve both for status diagnostics and runtime
        // policy application.
        var (pinnedManifest, pinsError) = Pins.TryLoadPinned(PinnedPath);

        var policy = settings is not null
            ? BoundaryPolicy.FromSettings(settings, exportsRoot)
            : BoundaryPolicy.CreateDefault(SettingsPath, exportsRoot);

        // --------------------------- /v1/status ---------------------------
        app.MapGet("/v1/status", async () =>
        {
            var capturedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

            // Probe Ollama for version and tags (best effort). If unreachable or error,
            // fields fall back to default values and reachability flag is false.
            bool ollamaReachable = false;
            string? ollamaVersionLocal = null;
            OllamaTags? tags = null;
            try
            {
                var ver = await StatusHttp.GetFromJsonAsync<OllamaVersion>($"{ollamaBaseUrl}/api/version");
                if (ver is not null)
                {
                    ollamaReachable = true;
                    ollamaVersionLocal = string.IsNullOrWhiteSpace(ver.Version) ? null : ver.Version;

                    try
                    {
                        tags = await StatusHttp.GetFromJsonAsync<OllamaTags>($"{ollamaBaseUrl}/api/tags");
                    }
                    catch
                    {
                        // tags unavailable; leave null
                    }
                }
            }
            catch
            {
                // unreachable; leave defaults
            }

            // Pins verification (spec 7.4.1):
            // - pins_match reflects manifest completeness; null only when manifest cannot be read.
            // - model_digests_match requires live tags; null when tags unavailable.
            bool? pinsMatch = pinnedManifest is null ? null : string.IsNullOrWhiteSpace(pinsError);

            bool? modelDigestsMatch = (pinnedManifest is not null && tags?.Models is not null)
                ? Pins.VerifyAgainstTags(pinnedManifest, tags)
                : null;

            string? pinsDetail = pinnedManifest is null
                ? (pinsError ?? "pinned.yaml missing or unreadable")
                : (string.IsNullOrWhiteSpace(pinsError) ? null : pinsError);

            // DB health: open and verify schema version equals 1. Capture any error.
            var dbHealthy = false;
            string? dbError = null;
            try
            {
                await using var conn = Db.Open(DbPath);
                await conn.OpenAsync();
                var v = await Db.ScalarAsync(conn, "SELECT value FROM meta WHERE key='schema_version' LIMIT 1;");
                dbHealthy = string.Equals((v ?? "").Trim(), "1", StringComparison.Ordinal);
            }
            catch (Exception ex)
            {
                dbHealthy = false;
                dbError = $"{ex.GetType().Name}: {ex.Message}";
            }

            // GPU evidence (best effort); surfaces as soon as one GPU is visible.
            bool gpuVisible = false;
            string? gpuEvidence = null;
            try
            {
                var (ok, outp) = Proc.TryRun("bash", "-lc \"nvidia-smi --query-gpu=name,driver_version --format=csv,noheader\"");
                gpuVisible = ok && !string.IsNullOrWhiteSpace(outp);
                gpuEvidence = gpuVisible ? outp.Trim() : null;
            }
            catch
            {
                // ignore
            }

            // Compose status response. The schema version is locked at 1 per spec.
            var status = new StatusResponse(
                SchemaVersion: 1,
                CapturedUtc: capturedUtc,
                Core: new StatusCoreInfo(true,
                    // Prefer assembly version; if null, surface empty string.
                    typeof(Program).Assembly.GetName().Version?.ToString() ?? "",
                    coreBindUrl),
                Ollama: new StatusOllamaInfo(ollamaReachable, ollamaVersionLocal, ollamaBaseUrl),
                Pins: new StatusPinsInfo(PinnedPath, pinsMatch, modelDigestsMatch, pinsDetail),
                Db: new StatusDbInfo(DbPath, dbHealthy, dbError),
                Gpu: new StatusGpuInfo(gpuVisible, gpuEvidence),
                Tailnet: new StatusTailnetInfo(false, null),
                Files: new StatusFilesInfo(File.Exists(SettingsPath), File.Exists(PinnedPath))
            );

            return Results.Json(status, options: StatusJsonOpts);
        });

        // ---------------------- POST /v1/conversations ----------------------
        app.MapPost("/v1/conversations", async (ConversationCreateRequest req) =>
        {
            if (!Validation.TryRoleTier(req.Role, req.Tier, out var role, out var tier, out var err))
                return ApiError.BadRequest(err!);

            // Ensure a manifest was loaded
            if (pinnedManifest is null)
                return ApiError.Failed(
                    "PIN_MISSING",
                    "Pinned model manifest is missing",
                    hint: "Ensure config/pinned.yaml exists and is valid."
                );

            // Determine the model to use based on strict/fallback policy. A null digest is considered
            // unpinned. In strict mode, unpinned roles/tiers result in an error. In fallback mode,
            // the server resolves to a fallback model while preserving the requested role and tier.
            PinnedEntry? chosen = null;
            if (pinnedManifest.Entries.TryGetValue((role, tier), out var entry) && !string.IsNullOrWhiteSpace(entry.Digest))
            {
                // Found a pinned digest; use it.
                chosen = entry;
            }
            else
            {
                // Either no entry or digest null/empty: this is unpinned.
                var mode = (settings?.Pins?.Mode ?? "strict").Trim().ToLowerInvariant();
                if (mode != "fallback")
                {
                    // Distinguish between no entry vs entry without digest for error clarity
                    if (!pinnedManifest.Entries.ContainsKey((role, tier)))
                    {
                        return ApiError.BadRequest(new ErrorResponse(
                            "PIN_NO_MATCH",
                            $"No pinned model for role={role} tier={tier}",
                            null,
                            "Update pinned.yaml model mapping."
                        ));
                    }
                    return ApiError.BadRequest(new ErrorResponse(
                        "PIN_UNPINNED",
                        $"Role={role} tier={tier} is unpinned (digest missing)",
                        null,
                        "Pin this model or enable fallback mode."
                    ));
                }

                // Fallback mode (spec 5.2): resolve to pins.fallback_role/pins.fallback_tier.
                var fbRole = (settings?.Pins?.FallbackRole ?? "").Trim().ToLowerInvariant();
                var fbTier = (settings?.Pins?.FallbackTier ?? "").Trim().ToLowerInvariant();
                if (string.IsNullOrWhiteSpace(fbRole) || string.IsNullOrWhiteSpace(fbTier))
                {
                    return ApiError.Failed(
                        "PIN_FALLBACK_INVALID",
                        "Fallback role/tier not configured",
                        hint: "Set pins.fallback_role and pins.fallback_tier in settings.yaml."
                    );
                }
                if (!pinnedManifest.Entries.TryGetValue((fbRole, fbTier), out var fbEntry) || string.IsNullOrWhiteSpace(fbEntry.Digest))
                {
                    return ApiError.Failed(
                        "PIN_FALLBACK_INVALID",
                        "Fallback pin missing or unpinned",
                        hint: $"Ensure {fbRole}.{fbTier} has a valid digest in pinned.yaml."
                    );
                }
                chosen = fbEntry;
            }

            // At this point 'chosen' is guaranteed non-null and has a non-null Digest.
            var tag = chosen!.Tag;
            var digest = chosen.Digest!;

            var title = (req.Title ?? "").Trim();
            if (title.Length == 0) title = $"{role}-{tier}";

            await using var conn = Db.Open(DbPath);
            await conn.OpenAsync();

            var createdUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
            var id = await Db.InsertConversationAsync(conn, createdUtc, role, tier, title, tag, digest);

            var dto = new ConversationDto(id, createdUtc, title, role, tier, tag, digest);
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
            // Determine resolution for this conversation.
            // Only report a fallback when the conversation digest matches the configured fallback digest (spec 10.3).
            string? resolution = null;
            if (pinnedManifest is not null)
            {
                if (pinnedManifest.Entries.TryGetValue((convo.Role, convo.Tier), out var ent) &&
                    !string.IsNullOrWhiteSpace(ent.Digest) &&
                    string.Equals(ent.Digest, convo.ModelDigest, StringComparison.Ordinal))
                {
                    resolution = null;
                }
                else
                {
                    var fbRole = (settings?.Pins?.FallbackRole ?? "").Trim().ToLowerInvariant();
                    var fbTier = (settings?.Pins?.FallbackTier ?? "").Trim().ToLowerInvariant();
                    if (!string.IsNullOrWhiteSpace(fbRole) &&
                        !string.IsNullOrWhiteSpace(fbTier) &&
                        pinnedManifest.Entries.TryGetValue((fbRole, fbTier), out var fbEnt) &&
                        !string.IsNullOrWhiteSpace(fbEnt.Digest) &&
                        string.Equals(fbEnt.Digest, convo.ModelDigest, StringComparison.Ordinal))
                    {
                        resolution = $"fallback:{fbRole}.{fbTier}";
                    }
                }
            }

            // Emit meta
            await Sse.WriteEventAsync(
                http.Response,
                "meta",
                new SseMetaEvent(req.ConversationId, assistantId, convo.ModelTag, convo.ModelDigest, resolution),
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

internal sealed record PinnedEntry(string Tag, string? Digest, bool Required);

internal sealed record PinnedManifest(
    int SchemaVersion,
    string CapturedUtc,
    string OllamaVersion,
    Dictionary<(string role, string tier), PinnedEntry> Entries
);

internal static class Pins
{
    /// <summary>
    /// Load and validate the pinned model manifest. Returns a manifest and null
    /// error on success. On failure, returns null manifest and a human-readable
    /// error message describing the issue. Unlike the previous line-based loader,
    /// this implementation preserves entries with null digests and propagates
    /// metadata like captured_utc and ollama.version. See Spec.md §7.3.1.
    /// </summary>
    public static (PinnedManifest? Manifest, string? Error) TryLoadPinned(string pinnedPath)
    {
        if (!File.Exists(pinnedPath))
            return (null, $"pinned.yaml missing: {pinnedPath}");

        var yaml = File.ReadAllText(pinnedPath, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(yaml))
            return (null, $"pinned.yaml is empty: {pinnedPath}");

        try
        {
            var deserializer = new YamlDotNet.Serialization.DeserializerBuilder()
                .WithNamingConvention(YamlDotNet.Serialization.NamingConventions.UnderscoredNamingConvention.Instance)
                .IgnoreUnmatchedProperties()
                .Build();

            var dto = deserializer.Deserialize<PinnedYamlDto>(yaml);
            if (dto is null)
                return (null, "pinned.yaml deserialized to null");

            if (dto.SchemaVersion != 1)
                return (null, $"pinned.yaml schema_version must be 1 (got {dto.SchemaVersion})");

            // captured_utc is required for determinism
            if (string.IsNullOrWhiteSpace(dto.CapturedUtc))
                return (null, "pinned.yaml captured_utc is required");

            if (dto.Ollama is null || string.IsNullOrWhiteSpace(dto.Ollama.Version))
                return (null, "pinned.yaml ollama.version is required");

            var entries = new Dictionary<(string, string), PinnedEntry>();
            var missingRequired = new List<string>();

            if (dto.Models is null)
                return (null, "pinned.yaml models section is missing");

            foreach (var (roleRaw, tiers) in dto.Models)
            {
                if (string.IsNullOrWhiteSpace(roleRaw) || tiers is null) continue;
                var role = roleRaw.Trim().ToLowerInvariant();

                foreach (var (tierRaw, entryDto) in tiers)
                {
                    if (string.IsNullOrWhiteSpace(tierRaw) || entryDto is null) continue;
                    var tier = tierRaw.Trim().ToLowerInvariant();

                    var tag = (entryDto.Tag ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(tag))
                        continue; // skip completely missing tag

                    string? digest = null;
                    if (!string.IsNullOrWhiteSpace(entryDto.Digest))
                    {
                        var digNorm = NormalizeDigest(entryDto.Digest);
                        if (digNorm.Length == 64 && IsHex(digNorm))
                            digest = digNorm;
                        else
                            digest = null; // treat invalid digest as null
                    }

                    var required = entryDto.Required ?? false;
                    if (required && string.IsNullOrWhiteSpace(digest))
                    {
                        missingRequired.Add($"{role}.{tier}");
                    }

                    entries[(role, tier)] = new PinnedEntry(tag, digest, required);
                }
            }

            if (entries.Count == 0)
                return (null, "pinned.yaml contains no model entries");

            if (missingRequired.Count > 0)
            {
                var err = $"missing digest for required pins: {string.Join(", ", missingRequired)}";
                // We still return the manifest so that status can surface partial info.
                var manifestPartial = new PinnedManifest(dto.SchemaVersion, dto.CapturedUtc, dto.Ollama.Version!, entries);
                return (manifestPartial, err);
            }

            var manifest = new PinnedManifest(dto.SchemaVersion, dto.CapturedUtc, dto.Ollama.Version!, entries);
            return (manifest, null);
        }
        catch (Exception ex)
        {
            return (null, $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    /// <summary>
    /// Verify that all pinned entries with non-null digests match the current
    /// digests reported by Ollama. Returns true only if every entry with a
    /// digest is present in tags.Models with an identical digest. See Spec.md §7.4.
    /// </summary>
    public static bool VerifyAgainstTags(PinnedManifest manifest, Aetherforge.Contracts.OllamaTags tags)
    {
        var live = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var m in tags.Models)
        {
            if (string.IsNullOrWhiteSpace(m.Name) || string.IsNullOrWhiteSpace(m.Digest)) continue;
            live[m.Name] = NormalizeDigest(m.Digest);
        }

        foreach (var entry in manifest.Entries.Values)
        {
            if (string.IsNullOrWhiteSpace(entry.Digest)) continue; // skip unpinned
            if (!live.TryGetValue(entry.Tag, out var got)) return false;
            if (!string.Equals(got, entry.Digest, StringComparison.Ordinal)) return false;
        }
        return true;
    }

    private sealed class PinnedYamlDto
    {
        public int SchemaVersion { get; init; }
        public string? CapturedUtc { get; init; }
        public PinnedOllamaDto? Ollama { get; init; }
        public Dictionary<string, Dictionary<string, PinnedEntryDto>>? Models { get; init; }
    }

    private sealed class PinnedOllamaDto { public string? Version { get; init; } }
    private sealed class PinnedEntryDto
    {
        public string? Tag { get; init; }
        public string? Digest { get; init; }
        public bool? Required { get; init; }
    }

    private static string NormalizeDigest(string s)
    {
        var t = s.Trim().Trim('"').ToLowerInvariant();
        return t.StartsWith("sha256:", StringComparison.Ordinal) ? t["sha256:".Length..] : t;
    }

    private static bool IsHex(string s)
        => s.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f'));
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
