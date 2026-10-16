using AITranslator.Exceptions;
using System.Net;

namespace AITranslator.Translator.Communicator;

internal enum OpenAIFailureKind
{
    ContextLengthExceeded,
    OutputTruncated,
    ContentFiltered,
    Refusal,
    EmptyResponse,
    ReasoningOnly,
    ReasoningDetected,
    MalformedResponse,
    InvalidRequest,
    Authentication,
    PermissionDenied,
    RateLimited,
    QuotaExceeded,
    TransientServer,
    Transport,
    Timeout,
    Cancelled
}

/// <summary>
/// Safe, classified failure: never includes response bodies, prompts or credentials.
/// ContextLengthExceeded / OutputTruncated require caller-directed batch handling;
/// the communicator never alters source text or increases output budgets.
/// </summary>
internal sealed class OpenAICommunicationException : KnownException
{
    public OpenAIFailureKind FailureKind { get; }
    public HttpStatusCode? StatusCode { get; }
    public string? RequestId { get; }
    public string? FinishReason { get; }

    public OpenAICommunicationException(OpenAIFailureKind failureKind, HttpStatusCode? statusCode = null,
        string? requestId = null, string? finishReason = null)
        : base($"OpenAI-compatible request failed: {failureKind}" +
            (statusCode.HasValue ? $" (HTTP {(int)statusCode.Value})" : string.Empty))
    {
        FailureKind = failureKind;
        StatusCode = statusCode;
        RequestId = requestId;
        FinishReason = finishReason;
    }
}
