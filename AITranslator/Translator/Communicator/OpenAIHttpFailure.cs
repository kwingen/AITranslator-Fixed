using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;

namespace AITranslator.Translator.Communicator;

internal static class OpenAIHttpFailure
{
    public static bool IsTransient(HttpStatusCode status) => (int)status is 408 or 429 or 500 or 502 or 503 or 504;

    public static OpenAICommunicationException Classify(HttpStatusCode status, string body, string? requestId)
    {
        OpenAIFailureKind kind = status switch
        {
            HttpStatusCode.Unauthorized => OpenAIFailureKind.Authentication,
            HttpStatusCode.Forbidden => OpenAIFailureKind.PermissionDenied,
            HttpStatusCode.TooManyRequests => OpenAIFailureKind.RateLimited,
            HttpStatusCode.RequestTimeout => OpenAIFailureKind.Timeout,
            _ when IsTransient(status) => OpenAIFailureKind.TransientServer,
            _ => OpenAIFailureKind.InvalidRequest
        };
        if ((int)status is 400 or 413 or 422 or 429)
        {
            try
            {
                JObject root = JObject.Parse(body);
                if (root["error"] is JObject error)
                {
                    string[] codes = new[] { "code", "type" }
                        .Select(name => error[name]?.ToString().ToLowerInvariant() ?? "").ToArray();
                    string message = error["message"]?.ToString().ToLowerInvariant() ?? "";
                    if ((int)status != 429 &&
                        (codes.Any(code => code is "context_length_exceeded" or "context_window_exceeded" or "prompt_too_long" or "exceed_context_size_error") ||
                         message.Contains("maximum context length") || message.Contains("context window exceeded") ||
                         message.Contains("prompt is too long") || message.Contains("exceeds the available context size")))
                        kind = OpenAIFailureKind.ContextLengthExceeded;
                    if ((int)status == 429 && codes.Any(code => code is "insufficient_quota" or "billing_hard_limit_reached"))
                        kind = OpenAIFailureKind.QuotaExceeded;
                }
            }
            catch (JsonException) { /* HTML / empty error bodies retain the HTTP classification. */ }
        }
        return new(kind, status, requestId);
    }
}
