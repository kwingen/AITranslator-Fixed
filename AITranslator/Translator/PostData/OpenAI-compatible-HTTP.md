# OpenAI-compatible HTTP settings

The original four-argument `OpenAICommunicator` constructor and synchronous
`ICommunicator.Translate(..., out speed)`, `Cancel()` and `Dispose()` contracts
are unchanged. HTTP and retry waits use async I/O internally; invoke translation
from the existing worker, not the UI thread.

## Extended parameters (existing ExpendedParams text box)

Both the legacy JSON-property fragment and a complete JSON object are accepted.
Properties replace optional defaults by name; the emitted request has no duplicate keys.
Transport-owned `messages` and `model` cannot be replaced or removed through extensions:
any such property is rejected before HTTP, so source/history and selected model remain authoritative.
Duplicate keys *inside the extension* and malformed JSON fail before any request.
A top-level `null` removes an optional request property instead of sending JSON null.
Nested provider objects are replaced as a whole and otherwise retained as supplied.

Example for a backend requiring `max_completion_tokens` and disallowing sampling:

```json
"max_completion_tokens": 512,
"temperature": null,
"top_p": null,
"frequency_penalty": null,
"presence_penalty": null,
"stop": null
```

A non-null explicit `max_completion_tokens` removes `max_tokens`, even when both
are supplied in the extension. Its value is used exactly as configured. No larger
output limit, source truncation, history truncation or automatic model change is
performed by this layer. Removing a limit without replacing it delegates the
limit to the server, so retain an explicit limit when cost matters.

Provider-specific objects continue to work, for example:

```json
{"extra_body":{"enable_thinking":false}}
```

Use the non-thinking setting supported by the actual backend; this example is
not assumed to be supported by every OpenAI-compatible server.

## Reasoning/final-content boundary

Separate `message.reasoning_content` and `message.reasoning` fields are never
promoted to translation. Final `message.content` must be a nonblank string.

Default behavior rejects a leading `<think>...</think>` or
`<reasoning>...</reasoning>` block rather than leaking reasoning or silently
deleting possible source markup. To opt into compatibility stripping:

```json
"_translator_reasoning": "strip_leading_think"
```

`"_translator_reasoning":"reject"` (or null/absent) selects the default.
This reserved client-only option is **not sent to the server**. Only complete,
unnested, leading blocks with exact bare tags (case-insensitive) can be stripped.
Non-leading balanced literal tags in final text remain untouched. Unmatched or mismatched
known reasoning delimiters (including backend-prefilled closing delimiters) fail closed.
Nested/attribute-bearing or ambiguous leading markup fails closed; unclosed or reasoning-only
output is rejected. No general-purpose stripping regex is applied to translation text.

## Responses and failures

A usable completion must have a first choice, `finish_reason:"stop"`, and a
nonempty final string. `length`, `content_filter`, refusal, empty choices/content,
non-JSON/wrong-shaped replies and unsupported finish reasons are not successes.
Duplicate JSON properties anywhere in a response are `MalformedResponse`, never last-key-wins.
Malformed/invalid optional usage data contributes zero speed rather than an
infinite/NaN speed. `completion_tokens`, when valid, is measured over the full
successful HTTP request/read duration.

`OpenAICommunicationException : KnownException` exposes:

- `FailureKind` (`OpenAIFailureKind`), including `ContextLengthExceeded`,
  `OutputTruncated`, `ContentFiltered`, `Refusal`, `EmptyResponse`,
  `ReasoningOnly`, `ReasoningDetected`, `MalformedResponse`, `InvalidRequest`,
  `Authentication`, `PermissionDenied`, `QuotaExceeded`, `RateLimited`,
  `TransientServer`, `Transport`, `Timeout`, `Cancelled`.
- `StatusCode` (`HttpStatusCode?`), `RequestId` (the `x-request-id` header), and
  `FinishReason` when available.

Callers may explicitly adapt history/batches for `ContextLengthExceeded` and
`OutputTruncated`; the HTTP layer does not resend either failure. Messages and
exceptions deliberately omit raw response bodies, input text, reasoning and
credentials. There is no dependency on the WPF log dispatcher in HTTP handling.

## Retries, cancellation and ownership

At most **three total attempts** are made. Retryable HTTP statuses are 408, 429,
500, 502, 503 and 504; network errors and timeouts also use the same bound.
Recognized permanent quota/billing errors do not consume retries. Other 4xx,
including 400/401/403 and context-length failures, are never blindly retried.

Backoff is 1 then 2 seconds, with additive 0–25% jitter. A valid Retry-After
(delta-seconds or HTTP-date) is a minimum, not a replacement that can shorten
backoff. A server delay over ten minutes surfaces the failure without retrying
earlier or waiting indefinitely. Retry waits and in-flight HTTP requests are
cancellable. A cancelled communicator stays cancelled; create a new instance to
resume, matching its original lifetime behavior. Retried network/timeout requests
can have reached the server already and may consume additional requests; no
exactly-once billing guarantee is possible.

Each attempt creates and disposes its own request, request content and response;
resources are released **before** retry waits. The production constructor disables
automatic redirects: configure the final endpoint directly. HTTP redirects are non-retried
`InvalidRequest` failures, so source/history is not forwarded to a redirected destination.
The original constructor owns its HttpClient. The injected-client overload is caller-owned
unless `ownsClient:true` is explicitly supplied, and injected clients retain caller-controlled
redirect policy. Dispose cancels outstanding work and is idempotent.

## Local regression tests (no paid calls)

`AITranslator.Tests/OpenAICommunicatorTests.cs` executes the actual communicator
with an injected HttpClient/HttpMessageHandler. Delays are injectable for bounded
retry assertions, while the cancellation-during-Retry-After regression uses the
real cancellable delay. All fixtures contain only synthetic source/response text
and an empty API key.

```text
dotnet test AITranslator.Tests/AITranslator.Tests.csproj --filter FullyQualifiedName~OpenAICommunicatorTests
```

For independent workers sharing one Windows WPF checkout, isolate build outputs:

```text
dotnet test AITranslator.Tests/AITranslator.Tests.csproj --filter FullyQualifiedName~OpenAICommunicatorTests -p:IntermediateOutputPath=obj/OpenAI/ -p:OutputPath=bin/OpenAI/
```

Do not change BaseIntermediateOutputPath just to isolate the run: restore assets
remain in the project's normal obj directory. Tests do not load a native model,
start the WPF UI, or contact a public/paid API.
