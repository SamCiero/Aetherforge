using System.Text.Json.Serialization;

namespace Aetherforge.Contracts;

// Primary data transfer objects (DTOs) used across the Core API. Moving these
// definitions into a dedicated contracts project ensures that both the server
// and any clients (e.g. Windows UI, command‑line tools) share a single
// canonical representation of the API.  This file contains the concrete
// contracts required for milestone M1.

/// <summary>
/// Request body for creating a new conversation.  The role and tier must
/// correspond to valid entries from <c>pinned.yaml</c> (general/coding/agent and
/// fast/thinking).  Title may be null or empty; a default will be derived
/// server side.
/// </summary>
public sealed record ConversationCreateRequest(string Role, string Tier, string? Title);

/// <summary>
/// Request body for patching an existing conversation.  Only the title can be
/// updated at this stage of the API.
/// </summary>
public sealed record ConversationPatchRequest(string? Title);

/// <summary>
/// Request body for sending a chat message.  <see cref="ConversationId"/>
/// identifies which conversation the message belongs to.
/// </summary>
public sealed record ChatRequest(
    [property: JsonPropertyName("conversation_id")] int ConversationId,
    string Content);

/// <summary>
/// Core representation of a conversation.  Includes the pinned model tag and
/// digest so that deterministic exports and replication are possible.
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
/// Representation of an individual message within a conversation.  Sender will
/// be either <c>user</c> or <c>assistant</c>.  <see cref="MetaJson"/> holds
/// optional metadata as a raw JSON string.
/// </summary>
public sealed record MessageDto(
    int Id,
    [property: JsonPropertyName("created_utc")] string CreatedUtc,
    string Sender,
    string Content,
    [property: JsonPropertyName("meta_json")] string? MetaJson);

/// <summary>
/// Wrapper for a conversation and its associated messages.  Used when
/// retrieving the full history of a conversation.
/// </summary>
public sealed record ConversationWithMessagesDto(
    ConversationDto Conversation,
    List<MessageDto> Messages);

/// <summary>
/// Response shape for listing conversations.  Supports pagination via limit
/// and offset parameters.  If a query was provided, it will be returned in
/// <c>Q</c> for caller convenience.
/// </summary>
public sealed record ConversationListResponse(
    List<ConversationDto> Items,
    int Limit,
    int Offset,
    string? Q);

/// <summary>
/// Canonical error response used for all API error conditions.  <see cref="Code"/>
/// contains a machine‑readable error code, <see cref="Message"/> is a brief
/// description, and <see cref="Detail"/> or <see cref="Hint"/> may carry
/// optional additional context for developers.
/// </summary>
public sealed record ErrorResponse(
    string Code,
    string Message,
    string? Detail = null,
    string? Hint = null);
