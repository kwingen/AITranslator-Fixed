using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace AITranslator.Translator.Communicator;

internal sealed record OpenAICompletion(string Content, string FinishReason, int? CompletionTokens);

internal static class OpenAICompletionParser
{
    public static OpenAICompletion Parse(string body, HttpStatusCode status, string? requestId = null, bool stripLeadingThink = false)
    {
        OpenAICommunicationException Fail(OpenAIFailureKind kind, string? finish = null) =>
            new(kind, status, requestId, finish);

        JObject root;
        try
        {
            root = JObject.Parse(body,
                new JsonLoadSettings { DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error });
        }
        catch (JsonException) { throw Fail(OpenAIFailureKind.MalformedResponse); }
        if (root["choices"] is not JArray choices)
            throw Fail(OpenAIFailureKind.MalformedResponse);
        if (choices.Count == 0)
            throw Fail(OpenAIFailureKind.EmptyResponse);
        if (choices[0] is not JObject choice || choice["finish_reason"]?.Type != JTokenType.String)
            throw Fail(OpenAIFailureKind.MalformedResponse);
        string finish = choice["finish_reason"]!.Value<string>()!;
        if (finish == "length") throw Fail(OpenAIFailureKind.OutputTruncated, finish);
        if (finish == "content_filter") throw Fail(OpenAIFailureKind.ContentFiltered, finish);
        if (finish == "refusal") throw Fail(OpenAIFailureKind.Refusal, finish);
        if (finish != "stop" || choice["message"] is not JObject message)
            throw Fail(OpenAIFailureKind.MalformedResponse, finish);
        if (message["refusal"] is { Type: not JTokenType.Null } refusal &&
            (refusal.Type != JTokenType.String || !string.IsNullOrWhiteSpace(refusal.Value<string>())))
            throw Fail(OpenAIFailureKind.Refusal, finish);
        bool hasReasoning = new[] { "reasoning_content", "reasoning" }.Any(name =>
            message[name] is { Type: not JTokenType.Null } value && !string.IsNullOrWhiteSpace(value.ToString()));
        JToken? content = message["content"];
        if (content is null || content.Type == JTokenType.Null)
            throw Fail(hasReasoning ? OpenAIFailureKind.ReasoningOnly : OpenAIFailureKind.EmptyResponse, finish);
        if (content.Type != JTokenType.String)
            throw Fail(OpenAIFailureKind.MalformedResponse, finish);
        string text = content.Value<string>()!.Trim();
        if (text.Length == 0)
            throw Fail(hasReasoning ? OpenAIFailureKind.ReasoningOnly : OpenAIFailureKind.EmptyResponse, finish);
        // Only known leading blocks are eligible. Never regex-remove markup from final content.
        while (LeadingReasoningTag(text) is string tag)
        {
            string open = "<" + tag + ">";
            string close = "</" + tag + ">";
            if (!text.StartsWith(open, StringComparison.OrdinalIgnoreCase))
                throw Fail(OpenAIFailureKind.MalformedResponse, finish);
            int end = text.IndexOf(close, StringComparison.OrdinalIgnoreCase);
            if (end < 0) throw Fail(OpenAIFailureKind.ReasoningOnly, finish);
            string reasoning = text[open.Length..end];
            if (reasoning.Contains("<think", StringComparison.OrdinalIgnoreCase) ||
                reasoning.Contains("<reasoning", StringComparison.OrdinalIgnoreCase))
                throw Fail(OpenAIFailureKind.MalformedResponse, finish);
            if (!stripLeadingThink) throw Fail(OpenAIFailureKind.ReasoningDetected, finish);
            text = text[(end + close.Length)..].Trim();
            if (text.Length == 0) throw Fail(OpenAIFailureKind.ReasoningOnly, finish);
            if (text.StartsWith("</think>", StringComparison.OrdinalIgnoreCase) ||
                text.StartsWith("</reasoning>", StringComparison.OrdinalIgnoreCase))
                throw Fail(OpenAIFailureKind.MalformedResponse, finish);
        }
        // A backend may prefill the opening tag and return only reasoning + its close.
        // Reject unmatched delimiters anywhere, without rewriting balanced literal markup.
        if (!HasBalancedReasoningDelimiters(text))
            throw Fail(OpenAIFailureKind.MalformedResponse, finish);
        int? tokens = null;
        if (root["usage"] is JObject usage && usage["completion_tokens"] is { Type: JTokenType.Integer } count &&
            int.TryParse(count.ToString(), out int number) && number >= 0)
            tokens = number;
        return new OpenAICompletion(text, finish, tokens);
    }

    private static bool HasBalancedReasoningDelimiters(string text)
    {
        var openTags = new Stack<string>();
        foreach (Match match in Regex.Matches(text, "<(/?)(think|reasoning)>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
        {
            string tag = match.Groups[2].Value;
            if (match.Groups[1].Value.Length == 0)
                openTags.Push(tag);
            else if (!openTags.TryPop(out string? open) ||
                !string.Equals(open, tag, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return openTags.Count == 0;
    }

    private static string? LeadingReasoningTag(string text)
    {
        foreach (string tag in new[] { "think", "reasoning" })
        {
            string prefix = "<" + tag;
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
                (text.Length == prefix.Length || text[prefix.Length] == '>' || char.IsWhiteSpace(text[prefix.Length])))
                return tag;
        }
        return null;
    }
}
