using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;

// Build and configure WebApplication
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("http://127.0.0.1:8484");

var app = builder.Build();

// Hard-coded paths (WSL view). These will later be loaded from settings.yaml.
const string SettingsPath = "/mnt/d/Aetherforge/config/settings.yaml";
const string PinnedPath = "/mnt/d/Aetherforge/config/pinned.yaml";
const string DbPath = "/var/lib/aetherforge/conversations.sqlite";
const string ExportsRoot = "/mnt/d/Aetherforge/exports";

// Ensure DB directory exists and schema initialized
await EnsureDatabaseAsync(DbPath);

// Parse pinned model mapping once at startup
var pinnedMapping = ParsePinnedMapping(PinnedPath);

// ------------------ Status Endpoint ------------------
app.MapGet("/v1/status", async () =>
{
    var capturedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");

    // Query Ollama for version and tags (best-effort)
    var ollamaReachable = false;
    string? ollamaVersion = null;
    OllamaTags? ollamaTags = null;
    try
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var ver = await http.GetFromJsonAsync<OllamaVersion>("http://127.0.0.1:11434/api/version");
        ollamaReachable = ver is not null && !string.IsNullOrWhiteSpace(ver.version);
        ollamaVersion = ver?.version;
        if (ollamaReachable)
        {
            ollamaTags = await http.GetFromJsonAsync<OllamaTags>("http://127.0.0.1:11434/api/tags");
        }
    }
    catch
    {
        ollamaReachable = false;
    }

    // Compute pins verification
    bool pinsMatch = false;
    bool modelDigestsMatch = false;
    try
    {
        (pinsMatch, modelDigestsMatch) = ComputePins(PinnedPath, ollamaTags);
    }
    catch
    {
        pinsMatch = false;
        modelDigestsMatch = false;
    }

    // Determine Ollama models dir from systemd env (WSL only)
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

    // Database health check
    bool dbHealthy = false;
    string? dbError = null;
    try
    {
        var csb = new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared };
        await using var conn = new SqliteConnection(csb.ToString());
        await conn.OpenAsync();
        // simple query to verify connection
        await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
        dbHealthy = true;
    }
    catch (Exception ex)
    {
        dbHealthy = false;
        dbError = ex.GetType().Name + ": " + ex.Message;
    }

    // GPU visibility (best-effort)
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
        pins = new { pinned_yaml_path = @"D:\Aetherforge\config\pinned.yaml", pins_match = pinsMatch, model_digests_match = modelDigestsMatch },
        db = new { path = DbPath, healthy = dbHealthy, error = dbError },
        gpu = new { visible = gpuVisible, evidence = gpuEvidence },
        tailnet = new { serve_enabled = false, published_port = (int?)null },
        files = new { settings_exists = File.Exists(SettingsPath), pinned_exists = File.Exists(PinnedPath) }
    };
    return Results.Json(payload);
});

// ------------------ Conversation Endpoints ------------------

// Create a new conversation
app.MapPost("/v1/conversations", async (HttpContext context) =>
{
    var createReq = await context.Request.ReadFromJsonAsync<ConversationCreateRequest>(cancellationToken: context.RequestAborted);
    if (createReq is null)
    {
        return Results.Json(new ErrorResponse("INVALID_REQUEST", "Request body is required"), statusCode: 400);
    }
    // Validate role and tier
    if (string.IsNullOrWhiteSpace(createReq.Role) || string.IsNullOrWhiteSpace(createReq.Tier))
    {
        return Results.Json(new ErrorResponse("INVALID_ROLE_TIER", "Both role and tier are required"), statusCode: 400);
    }
    var role = createReq.Role.Trim().ToLowerInvariant();
    var tier = createReq.Tier.Trim().ToLowerInvariant();
    // Derive model tag/digest from pinned mapping
    if (!pinnedMapping.TryGetValue((role, tier), out var modelInfo))
    {
        return Results.Json(new ErrorResponse("UNKNOWN_ROLE_TIER", $"No pinned model for role '{createReq.Role}' and tier '{createReq.Tier}'"), statusCode: 400);
    }
    // Insert conversation
    int convoId;
    var createdUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString()))
    {
        await conn.OpenAsync(context.RequestAborted);
        await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
        var cmd = conn.CreateCommand();
        cmd.CommandText = @"INSERT INTO conversations(created_utc, title, role, tier, model_tag, model_digest) VALUES($created_utc,$title,$role,$tier,$model_tag,$model_digest); SELECT last_insert_rowid();";
        cmd.Parameters.AddWithValue("$created_utc", createdUtc);
        cmd.Parameters.AddWithValue("$title", (object?)createReq.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$role", role);
        cmd.Parameters.AddWithValue("$tier", tier);
        cmd.Parameters.AddWithValue("$model_tag", modelInfo.Tag);
        cmd.Parameters.AddWithValue("$model_digest", modelInfo.Digest);
        var result = await cmd.ExecuteScalarAsync(context.RequestAborted);
        convoId = Convert.ToInt32(result);
    }
    var conversationDto = new ConversationDto(convoId, createdUtc, createReq.Title, role, tier, modelInfo.Tag, modelInfo.Digest);
    return Results.Json(conversationDto);
});

// Get a conversation and its messages
app.MapGet("/v1/conversations/{id:int}", async (int id) =>
{
    await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString());
    await conn.OpenAsync();
    await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
    // Fetch conversation
    var convoCmd = conn.CreateCommand();
    convoCmd.CommandText = "SELECT id, created_utc, title, role, tier, model_tag, model_digest FROM conversations WHERE id=$id LIMIT 1;";
    convoCmd.Parameters.AddWithValue("$id", id);
    await using var reader = await convoCmd.ExecuteReaderAsync();
    if (!await reader.ReadAsync())
    {
        return Results.Json(new ErrorResponse("NOT_FOUND", $"Conversation {id} not found"), statusCode: 404);
    }
    var convo = new ConversationDto(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
    await reader.CloseAsync();
    // Fetch messages
    var msgCmd = conn.CreateCommand();
    msgCmd.CommandText = "SELECT id, created_utc, sender, content, meta_json FROM messages WHERE conversation_id=$cid ORDER BY id ASC;";
    msgCmd.Parameters.AddWithValue("$cid", id);
    var messages = new List<MessageDto>();
    await using var mreader = await msgCmd.ExecuteReaderAsync();
    while (await mreader.ReadAsync())
    {
        messages.Add(new MessageDto(mreader.GetInt32(0), mreader.GetString(1), mreader.GetString(2), mreader.GetString(3), mreader.IsDBNull(4) ? null : mreader.GetString(4)));
    }
    var combined = new ConversationWithMessagesDto(convo, messages);
    return Results.Json(combined);
});

// List conversations with paging and optional search
app.MapGet("/v1/conversations", async (HttpContext context) =>
{
    var query = context.Request.Query;
    var limit = query.TryGetValue("limit", out var limStr) && int.TryParse(limStr, out var lVal) ? Math.Min(Math.Max(lVal, 1), 100) : 20;
    var offset = query.TryGetValue("offset", out var offStr) && int.TryParse(offStr, out var oVal) ? Math.Max(oVal, 0) : 0;
    var q = query.TryGetValue("q", out var qStr) ? qStr.ToString() : null;
    await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString());
    await conn.OpenAsync();
    await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
    // Count total
    int total;
    var countCmd = conn.CreateCommand();
    if (!string.IsNullOrWhiteSpace(q))
    {
        countCmd.CommandText = "SELECT COUNT(*) FROM conversations WHERE title LIKE $q ESCAPE '\\'";
        countCmd.Parameters.AddWithValue("$q", "%" + EscapeLike(q!) + "%");
    }
    else
    {
        countCmd.CommandText = "SELECT COUNT(*) FROM conversations";
    }
    total = Convert.ToInt32(await countCmd.ExecuteScalarAsync());
    // Fetch items
    var listCmd = conn.CreateCommand();
    if (!string.IsNullOrWhiteSpace(q))
    {
        listCmd.CommandText = "SELECT id, created_utc, title, role, tier, model_tag, model_digest FROM conversations WHERE title LIKE $q ESCAPE '\\' ORDER BY id DESC LIMIT $limit OFFSET $offset";
        listCmd.Parameters.AddWithValue("$q", "%" + EscapeLike(q!) + "%");
    }
    else
    {
        listCmd.CommandText = "SELECT id, created_utc, title, role, tier, model_tag, model_digest FROM conversations ORDER BY id DESC LIMIT $limit OFFSET $offset";
    }
    listCmd.Parameters.AddWithValue("$limit", limit);
    listCmd.Parameters.AddWithValue("$offset", offset);
    var items = new List<ConversationDto>();
    await using var lreader = await listCmd.ExecuteReaderAsync();
    while (await lreader.ReadAsync())
    {
        items.Add(new ConversationDto(lreader.GetInt32(0), lreader.GetString(1), lreader.IsDBNull(2) ? null : lreader.GetString(2), lreader.GetString(3), lreader.GetString(4), lreader.GetString(5), lreader.GetString(6)));
    }
    var response = new ConversationListResponse(items, limit, offset, total, offset + items.Count < total ? offset + items.Count : (int?)null);
    return Results.Json(response);
});

// Update conversation metadata (title)
app.MapMethods("/v1/conversations/{id:int}", new[] { "PATCH" }, async (int id, HttpContext context) =>
{
    var doc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
    if (!doc.RootElement.TryGetProperty("title", out var titleElem) || titleElem.ValueKind != JsonValueKind.String)
    {
        return Results.Json(new ErrorResponse("INVALID_REQUEST", "Missing 'title' field"), statusCode: 400);
    }
    var newTitle = titleElem.GetString();
    if (newTitle is null)
    {
        return Results.Json(new ErrorResponse("INVALID_REQUEST", "Title must be a string"), statusCode: 400);
    }
    await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString());
    await conn.OpenAsync(context.RequestAborted);
    await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
    var updateCmd = conn.CreateCommand();
    updateCmd.CommandText = "UPDATE conversations SET title=$title WHERE id=$id";
    updateCmd.Parameters.AddWithValue("$title", newTitle);
    updateCmd.Parameters.AddWithValue("$id", id);
    var rows = await updateCmd.ExecuteNonQueryAsync(context.RequestAborted);
    if (rows == 0)
    {
        return Results.Json(new ErrorResponse("NOT_FOUND", $"Conversation {id} not found"), statusCode: 404);
    }
    return Results.Json(new { id, title = newTitle });
});

// ------------------ Chat Endpoint (SSE) ------------------
app.MapPost("/v1/chat", async (HttpContext context) =>
{
    var chatReq = await context.Request.ReadFromJsonAsync<ChatRequest>(cancellationToken: context.RequestAborted);
    if (chatReq is null)
    {
        context.Response.StatusCode = 400;
        await WriteSseError(context, new ErrorResponse("INVALID_REQUEST", "Request body is required"));
        return;
    }
    // If conversation_id is provided, fetch conversation; else create a new conversation using role/tier
    ConversationDto convo;
    if (chatReq.ConversationId.HasValue)
    {
        // retrieve existing conversation
        await using var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString());
        await conn.OpenAsync(context.RequestAborted);
        await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
        var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id, created_utc, title, role, tier, model_tag, model_digest FROM conversations WHERE id=$id LIMIT 1";
        cmd.Parameters.AddWithValue("$id", chatReq.ConversationId.Value);
        await using var reader = await cmd.ExecuteReaderAsync(context.RequestAborted);
        if (!await reader.ReadAsync())
        {
            context.Response.StatusCode = 404;
            await WriteSseError(context, new ErrorResponse("NOT_FOUND", $"Conversation {chatReq.ConversationId.Value} not found"));
            return;
        }
        convo = new ConversationDto(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
    }
    else
    {
        // create a conversation if role and tier specified
        if (string.IsNullOrWhiteSpace(chatReq.Role) || string.IsNullOrWhiteSpace(chatReq.Tier))
        {
            context.Response.StatusCode = 400;
            await WriteSseError(context, new ErrorResponse("INVALID_ROLE_TIER", "Either provide conversation_id or specify role and tier"));
            return;
        }
        var role = chatReq.Role!.Trim().ToLowerInvariant();
        var tier = chatReq.Tier!.Trim().ToLowerInvariant();
        if (!pinnedMapping.TryGetValue((role, tier), out var model))
        {
            context.Response.StatusCode = 400;
            await WriteSseError(context, new ErrorResponse("UNKNOWN_ROLE_TIER", $"No pinned model for role '{chatReq.Role}' and tier '{chatReq.Tier}'"));
            return;
        }
        int convoId;
        var createdUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString()))
        {
            await conn.OpenAsync(context.RequestAborted);
            await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
            var insert = conn.CreateCommand();
            insert.CommandText = @"INSERT INTO conversations(created_utc, title, role, tier, model_tag, model_digest) VALUES($created_utc,$title,$role,$tier,$model_tag,$model_digest); SELECT last_insert_rowid();";
            insert.Parameters.AddWithValue("$created_utc", createdUtc);
            insert.Parameters.AddWithValue("$title", (object?)null ?? DBNull.Value);
            insert.Parameters.AddWithValue("$role", role);
            insert.Parameters.AddWithValue("$tier", tier);
            insert.Parameters.AddWithValue("$model_tag", model.Tag);
            insert.Parameters.AddWithValue("$model_digest", model.Digest);
            var newId = await insert.ExecuteScalarAsync(context.RequestAborted);
            convoId = Convert.ToInt32(newId);
        }
        convo = new ConversationDto(convoId, createdUtc, null, role, tier, model.Tag, model.Digest);
    }
    // Insert user message
    int userMessageId;
    var userCreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    var userContent = chatReq.Content ?? string.Empty;
    await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString()))
    {
        await conn.OpenAsync(context.RequestAborted);
        await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
        var ins = conn.CreateCommand();
        ins.CommandText = @"INSERT INTO messages(conversation_id, created_utc, sender, content, meta_json) VALUES($cid,$created_utc,$sender,$content,$meta_json); SELECT last_insert_rowid();";
        ins.Parameters.AddWithValue("$cid", convo.Id);
        ins.Parameters.AddWithValue("$created_utc", userCreatedUtc);
        ins.Parameters.AddWithValue("$sender", "user");
        ins.Parameters.AddWithValue("$content", userContent);
        ins.Parameters.AddWithValue("$meta_json", (object?)null ?? DBNull.Value);
        var rid = await ins.ExecuteScalarAsync(context.RequestAborted);
        userMessageId = Convert.ToInt32(rid);
    }
    // Setup SSE response
    context.Response.StatusCode = 200;
    context.Response.Headers["Content-Type"] = "text/event-stream";
    await context.Response.Body.FlushAsync(context.RequestAborted);
    // Determine model info
    var modelTag = convo.ModelTag;
    var modelDigest = convo.ModelDigest;
    // Send meta event
    var metaPayload = new { conversation_id = convo.Id, model_tag = modelTag, model_digest = modelDigest };
    await WriteSseEvent(context, "meta", JsonSerializer.Serialize(metaPayload));
    // Call Ollama API (best-effort; stream disabled for now)
    string assistantAccumulated = string.Empty;
    try
    {
        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var requestBody = new
        {
            model = modelTag,
            prompt = userContent,
            stream = false
        };
        var reqContent = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
        var response = await http.PostAsync("http://127.0.0.1:11434/api/generate", reqContent, context.RequestAborted);
        if (!response.IsSuccessStatusCode)
        {
            // error from Ollama
            var errMsg = await response.Content.ReadAsStringAsync(context.RequestAborted);
            await WriteSseError(context, new ErrorResponse("OLLAMA_ERROR", $"Ollama returned {(int)response.StatusCode}: {errMsg}"));
            return;
        }
        var respJson = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(await response.Content.ReadAsStreamAsync(context.RequestAborted), cancellationToken: context.RequestAborted);
        if (respJson != null && respJson.TryGetValue("response", out var respElem))
        {
            assistantAccumulated = respElem.GetString() ?? string.Empty;
            // send as delta (single chunk)
            await WriteSseEvent(context, "delta", JsonSerializer.Serialize(new { message_id = 0, delta_text = assistantAccumulated }));
        }
    }
    catch (Exception ex)
    {
        await WriteSseError(context, new ErrorResponse("OLLAMA_ERROR", ex.Message));
        return;
    }
    // Insert assistant message into DB
    var assistantCreatedUtc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
    int assistantMessageId;
    await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadWrite, Cache = SqliteCacheMode.Shared }.ToString()))
    {
        await conn.OpenAsync(context.RequestAborted);
        await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
        var insA = conn.CreateCommand();
        insA.CommandText = @"INSERT INTO messages(conversation_id, created_utc, sender, content, meta_json) VALUES($cid,$created_utc,$sender,$content,$meta_json); SELECT last_insert_rowid();";
        insA.Parameters.AddWithValue("$cid", convo.Id);
        insA.Parameters.AddWithValue("$created_utc", assistantCreatedUtc);
        insA.Parameters.AddWithValue("$sender", "assistant");
        insA.Parameters.AddWithValue("$content", assistantAccumulated);
        insA.Parameters.AddWithValue("$meta_json", (object?)null ?? DBNull.Value);
        var rid = await insA.ExecuteScalarAsync(context.RequestAborted);
        assistantMessageId = Convert.ToInt32(rid);
    }
    // Send done event
    await WriteSseEvent(context, "done", JsonSerializer.Serialize(new { message_id = assistantMessageId }));
});

// ------------------ Export Endpoint ------------------
app.MapPost("/v1/export/{id:int}", async (int id) =>
{
    // Read conversation and messages
    ConversationDto? convo;
    List<MessageDto> messages;
    await using (var conn = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = DbPath, Mode = SqliteOpenMode.ReadOnly, Cache = SqliteCacheMode.Shared }.ToString()))
    {
        await conn.OpenAsync();
        await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
        var ccmd = conn.CreateCommand();
        ccmd.CommandText = "SELECT id, created_utc, title, role, tier, model_tag, model_digest FROM conversations WHERE id=$id LIMIT 1";
        ccmd.Parameters.AddWithValue("$id", id);
        await using var reader = await ccmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync())
        {
            return Results.Json(new ErrorResponse("NOT_FOUND", $"Conversation {id} not found"), statusCode: 404);
        }
        convo = new ConversationDto(reader.GetInt32(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetString(5), reader.GetString(6));
        await reader.CloseAsync();
        // messages
        var mcmd = conn.CreateCommand();
        mcmd.CommandText = "SELECT id, created_utc, sender, content, meta_json FROM messages WHERE conversation_id=$cid ORDER BY id ASC";
        mcmd.Parameters.AddWithValue("$cid", id);
        messages = new List<MessageDto>();
        await using var mreader = await mcmd.ExecuteReaderAsync();
        while (await mreader.ReadAsync())
        {
            messages.Add(new MessageDto(mreader.GetInt32(0), mreader.GetString(1), mreader.GetString(2), mreader.GetString(3), mreader.IsDBNull(4) ? null : mreader.GetString(4)));
        }
    }
    // Determine export folder path
    var dateFolder = DateTime.UtcNow.ToString("yyyy-MM-dd");
    var folderPath = Path.Combine(ExportsRoot, dateFolder);
    Directory.CreateDirectory(folderPath);
    var titleSlug = GenerateSlug(convo!.Title ?? "untitled");
    var baseFilename = $"{convo.Id}.{titleSlug}";
    var jsonPath = Path.Combine(folderPath, baseFilename + ".json");
    var mdPath = Path.Combine(folderPath, baseFilename + ".md");
    // Compose JSON
    var exportObj = new
    {
        schema_version = 1,
        generated_utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
        core_version = "unknown",
        conversation = new { id = convo.Id, created_utc = convo.CreatedUtc, title = convo.Title, role = convo.Role, tier = convo.Tier },
        model = new { tag = convo.ModelTag, digest = convo.ModelDigest },
        messages = messages.Select(m => new { id = m.Id, created_utc = m.CreatedUtc, sender = m.Sender, content = m.Content, meta_json = m.MetaJson }).ToList()
    };
    await File.WriteAllTextAsync(jsonPath, JsonSerializer.Serialize(exportObj, new JsonSerializerOptions { WriteIndented = true }));
    // Compose Markdown
    var sb = new StringBuilder();
    sb.AppendLine($"# Conversation {convo.Id}");
    sb.AppendLine();
    sb.AppendLine($"- Title: {convo.Title ?? "(untitled)"}");
    sb.AppendLine($"- Role/Tier: {convo.Role}/{convo.Tier}");
    sb.AppendLine($"- Model: {convo.ModelTag} ({convo.ModelDigest})");
    sb.AppendLine($"- Created: {convo.CreatedUtc}");
    sb.AppendLine();
    sb.AppendLine("## Messages");
    sb.AppendLine();
    foreach (var m in messages)
    {
        sb.AppendLine($"**{m.Sender}** ({m.CreatedUtc}):");
        sb.AppendLine(m.Content);
        sb.AppendLine();
    }
    await File.WriteAllTextAsync(mdPath, sb.ToString());
    return Results.Json(new { json_path = jsonPath, md_path = mdPath });
});

app.Run();

// ------------------ Helper Types and Functions ------------------

record ConversationCreateRequest(string Role, string Tier, string? Title);
record ConversationDto(int Id, string CreatedUtc, string? Title, string Role, string Tier, string ModelTag, string ModelDigest);
record MessageDto(int Id, string CreatedUtc, string Sender, string Content, string? MetaJson);
record ConversationWithMessagesDto(ConversationDto Conversation, List<MessageDto> Messages);
record ConversationListResponse(List<ConversationDto> Items, int Limit, int Offset, int Total, int? NextOffset);
record ChatRequest([property: JsonPropertyName("conversation_id")] int? ConversationId, string? Role, string? Tier, string? Content);
record ErrorResponse(string Code, string Message, string? Detail = null, string? Hint = null);
record ModelInfo(string Tag, string Digest);
sealed record OllamaVersion(string version);
sealed record OllamaTags(List<OllamaModel> models);
sealed record OllamaModel(string? name, string? digest);

static async Task EnsureDatabaseAsync(string dbPath)
{
    Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
    var csb = new SqliteConnectionStringBuilder { DataSource = dbPath, Mode = SqliteOpenMode.ReadWriteCreate, Cache = SqliteCacheMode.Shared };
    await using var conn = new SqliteConnection(csb.ToString());
    await conn.OpenAsync();
    await ExecAsync(conn, "PRAGMA journal_mode=WAL;");
    await ExecAsync(conn, "PRAGMA synchronous=NORMAL;");
    await ExecAsync(conn, "PRAGMA busy_timeout=5000;");
    // meta table
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
    // conversations table
    await ExecAsync(conn, """
        CREATE TABLE IF NOT EXISTS conversations (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          created_utc TEXT NOT NULL,
          title TEXT NULL,
          role TEXT NOT NULL,
          tier TEXT NOT NULL,
          model_tag TEXT NOT NULL,
          model_digest TEXT NOT NULL
        );
        """);
    // messages table
    await ExecAsync(conn, """
        CREATE TABLE IF NOT EXISTS messages (
          id INTEGER PRIMARY KEY AUTOINCREMENT,
          conversation_id INTEGER NOT NULL,
          created_utc TEXT NOT NULL,
          sender TEXT NOT NULL,
          content TEXT NOT NULL,
          meta_json TEXT NULL,
          FOREIGN KEY(conversation_id) REFERENCES conversations(id) ON DELETE CASCADE
        );
        """);
}

static Dictionary<(string role, string tier), ModelInfo> ParsePinnedMapping(string pinnedPath)
{
    var map = new Dictionary<(string, string), ModelInfo>(StringComparer.Ordinal);
    if (!File.Exists(pinnedPath)) return map;
    string? role = null;
    string? tier = null;
    string? tag = null;
    string? digest = null;
    foreach (var raw in File.ReadLines(pinnedPath))
    {
        var line = raw.Trim();
        if (line.StartsWith("#") || line.Length == 0) continue;
        if (line.EndsWith(":"))
        {
            // role or tier
            var key = line[..^1];
            if (role is null)
            {
                role = key.Trim().ToLowerInvariant();
            }
            else
            {
                tier = key.Trim().ToLowerInvariant();
            }
            continue;
        }
        if (line.StartsWith("tag:", StringComparison.Ordinal))
        {
            tag = ExtractYamlScalar(line.Substring("tag:".Length));
            continue;
        }
        if (line.StartsWith("digest:", StringComparison.Ordinal))
        {
            digest = NormalizeDigest(ExtractYamlScalar(line.Substring("digest:".Length)));
        }
        // When tag and digest captured, assign and reset tier
        if (role is not null && tier is not null && tag is not null && digest is not null)
        {
            map[(role, tier)] = new ModelInfo(tag, digest);
            tag = null;
            digest = null;
            tier = null;
        }
    }
    return map;
}

static (bool pinsMatch, bool modelDigestsMatch) ComputePins(string pinnedPath, OllamaTags? tags)
{
    if (!File.Exists(pinnedPath)) return (false, false);
    var want = new Dictionary<string, string>(StringComparer.Ordinal);
    string? currentTag = null;
    foreach (var raw in File.ReadLines(pinnedPath))
    {
        var line = raw.Trim();
        if (line.StartsWith("tag:", StringComparison.Ordinal))
        {
            currentTag = ExtractYamlScalar(line.Substring("tag:".Length));
            continue;
        }
        if (currentTag is not null && line.StartsWith("digest:", StringComparison.Ordinal))
        {
            var dig = ExtractYamlScalar(line.Substring("digest:".Length));
            if (!string.IsNullOrWhiteSpace(currentTag) && !string.IsNullOrWhiteSpace(dig))
                want[currentTag] = NormalizeDigest(dig);
            currentTag = null;
        }
    }
    if (want.Count == 0) return (false, false);
    if (tags?.models is null) return (true, false);
    var have = new Dictionary<string, string>(StringComparer.Ordinal);
    foreach (var m in tags.models)
    {
        if (string.IsNullOrWhiteSpace(m.name) || string.IsNullOrWhiteSpace(m.digest)) continue;
        have[m.name] = NormalizeDigest(m.digest);
    }
    var ok = true;
    foreach (var kv in want)
    {
        if (!have.TryGetValue(kv.Key, out var live) || !string.Equals(live, kv.Value, StringComparison.Ordinal))
        {
            ok = false;
            break;
        }
    }
    return (true, ok);
}

static string ExtractYamlScalar(string s)
{
    s = s.Trim();
    if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1];
    return s.Trim();
}

static string NormalizeDigest(string s)
{
    s = s.Trim().Trim('"').ToLowerInvariant();
    return s.StartsWith("sha256:", StringComparison.Ordinal) ? s["sha256:".Length..] : s;
}

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

static string EscapeLike(string input)
{
    // Escape SQLite wildcards
    return input.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
}

static string GenerateSlug(string title)
{
    var sb = new StringBuilder();
    foreach (var ch in title.ToLowerInvariant())
    {
        if (char.IsLetterOrDigit(ch)) sb.Append(ch);
        else if (ch == ' ' || ch == '-' || ch == '_') sb.Append('-');
    }
    var slug = sb.ToString().Trim('-');
    if (slug.Length == 0) slug = "untitled";
    if (slug.Length > 64) slug = slug[..64];
    return slug;
}

static async Task WriteSseEvent(HttpContext context, string eventName, string json)
{
    var writer = context.Response.BodyWriter;
    var data = Encoding.UTF8.GetBytes($"event: {eventName}\ndata: {json}\n\n");
    await writer.WriteAsync(data, context.RequestAborted);
    await writer.FlushAsync(context.RequestAborted);
}

static async Task WriteSseError(HttpContext context, ErrorResponse error)
{
    var payload = JsonSerializer.Serialize(error);
    await WriteSseEvent(context, "error", payload);
}
