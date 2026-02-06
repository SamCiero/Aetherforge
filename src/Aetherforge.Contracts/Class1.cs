// D:/Aetherforge/src/Aetherforge.Contracts/Class1.cs

using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Aetherforge.Contracts;

// Primary data transfer objects (DTOs) used across the Core API. Moving these
// definitions into a dedicated contracts project ensures that both the server
// and any clients (e.g. Windows UI, command-line tools) share a single
// canonical representation of the API.

/// <summary>
/// Request body for creating a new conversation. The role and tier must be within
/// the supported enums (general/coding/agent and fast/thinking). Title may be null
/// or empty; a default will be derived server side.
///
/// Note: the server may be configured with a pins policy. In fallback mode, if the
/// requested role/tier is not pinned, the server resolves to a configured fallback
/// model (typically general.fast) while preserving the requested role/tier on the
/// conversation.
/// </summary>
public sealed record ConversationCreateRequest(string Role, string Tier, string? Title);

/// <summary>
/// Request body for patching an existing conversation. Only the title can be updated.
/// </summary>
public sealed record ConversationPatchRequest(string? Title);

/// <summary>
/// Request body for sending a chat message. <see cref="ConversationId"/> identifies
/// which conversation the message belongs to.
/// </summary>
public sealed record ChatRequest(
    [property: JsonPropertyName("conversation_id")] int ConversationId,
    string Content);

/// <summary>
/// Core representation of a conversation. Includes the resolved model tag and digest
/// so deterministic exports and replication are possible.
/// </summary>
public sealed record ConversationDto(
    int Id,
    [property: JsonPropertyName("created_utc")] string CreatedUtc,
    string Title,
    string Role,
    string Tier,
    [property: JsonPropertyName("model_tag")] string ModelTag,
    [property: JsonPropertyName("model_digest")] string ModelDigest);

/// <summary>
/// Representation of an individual message within a conversation. Sender will be
/// either <c>user</c> or <c>assistant</c>. <see cref="MetaJson"/> holds optional
/// metadata as a raw JSON string.
/// </summary>
public sealed record MessageDto(
    int Id,
    [property: JsonPropertyName("created_utc")] string CreatedUtc,
    string Sender,
    string Content,
    [property: JsonPropertyName("meta_json")] string? MetaJson);

/// <summary>
/// Wrapper for a conversation and its associated messages.
/// </summary>
public sealed record ConversationWithMessagesDto(
    ConversationDto Conversation,
    List<MessageDto> Messages);

/// <summary>
/// Response shape for listing conversations. Supports pagination via limit and offset.
/// If a query was provided, it will be returned in <c>Q</c>.
/// </summary>
public sealed record ConversationListResponse(
    List<ConversationDto> Items,
    int Limit,
    int Offset,
    string? Q);

/// <summary>
/// Canonical error response used for all API error conditions.
/// </summary>
public sealed record ErrorResponse(
    string Code,
    string Message,
    string? Detail = null,
    string? Hint = null);

// ---------------------------
// /v1/status
// ---------------------------

public sealed record StatusResponse(
    [property: JsonPropertyName("utc")] string Utc,
    StatusCoreInfo Core,
    StatusDbInfo Db,
    StatusOllamaInfo Ollama,
    StatusPinsInfo Pins);

public sealed record StatusCoreInfo(
    bool Reachable,
    string Version,
    [property: JsonPropertyName("base_url")] string BaseUrl);

public sealed record StatusDbInfo(
    string Path,
    [property: JsonPropertyName("wal_mode")] bool WalMode,
    [property: JsonPropertyName("busy_timeout_ms")] int BusyTimeoutMs);

public sealed record StatusOllamaInfo(
    bool Reachable,
    string Version,
    [property: JsonPropertyName("base_url")] string BaseUrl,
    // Optional/forward-compatible: some deployments can surface this; others cannot.
    [property: JsonPropertyName("models_dir")] string? ModelsDir = null);

public sealed record StatusPinsInfo(
    [property: JsonPropertyName("pins_match")] bool? PinsMatch,
    [property: JsonPropertyName("model_digests_match")] bool? ModelDigestsMatch,
    string? Detail);

// ---------------------------
// Ollama tags (used by /v1/status)
// ---------------------------

public sealed record OllamaTags(
    [property: JsonPropertyName("models")] List<OllamaTagModel> Models);

public sealed record OllamaTagModel(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("digest")] string Digest);

// ---------------------------
// SSE events (POST /v1/chat)
// ---------------------------

public sealed record SseMetaEvent(
    [property: JsonPropertyName("conversation_id")] int ConversationId,
    [property: JsonPropertyName("message_id")] int MessageId,
    [property: JsonPropertyName("model_tag")] string ModelTag,
    [property: JsonPropertyName("model_digest")] string ModelDigest,
    // Optional: present when server resolves via fallback pins policy (e.g. "fallback:general.fast").
    [property: JsonPropertyName("resolution")] string? Resolution = null);

public sealed record SseDeltaEvent(
    [property: JsonPropertyName("message_id")] int MessageId,
    [property: JsonPropertyName("delta_text")] string DeltaText);

public sealed record SseDoneEvent(
    [property: JsonPropertyName("message_id")] int MessageId);
