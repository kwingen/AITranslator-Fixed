using System.Net;
using System.Net.Http;
using System.Text;
using AITranslator.Exceptions;
using AITranslator.Translator.Communicator;
using AITranslator.Translator.Models;
using AITranslator.Translator.PostData;
using Newtonsoft.Json.Linq;

namespace AITranslator.Tests;

public class OpenAICommunicatorTests
{
    [Fact]
    public void LengthLimitedCompletionIsRejectedInsteadOfReturnedAsTranslation()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(
            "{\"choices\":[{\"finish_reason\":\"length\",\"message\":{\"content\":\"partial\"}}]}")));
        using var communicator = CreateCommunicator(handler);

        var error = Record.Exception(() => Translate(communicator));

        Assert.NotNull(error);
        Assert.IsAssignableFrom<KnownException>(error);
        var classified = Assert.IsType<OpenAICommunicationException>(error);
        Assert.Equal(OpenAIFailureKind.OutputTruncated, classified.FailureKind);
        Assert.Equal("length", classified.FinishReason);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("{\"choices\":[]}", OpenAIFailureKind.EmptyResponse)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"content_filter\",\"message\":{\"content\":\"partial\"}}]}", OpenAIFailureKind.ContentFiltered)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"partial\",\"refusal\":\"declined\"}}]}", OpenAIFailureKind.Refusal)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"refusal\",\"message\":{\"content\":\"partial\"}}]}", OpenAIFailureKind.Refusal)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"  \"}}]}", OpenAIFailureKind.EmptyResponse)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null}}]}", OpenAIFailureKind.EmptyResponse)]
    [InlineData("not JSON", OpenAIFailureKind.MalformedResponse)]
    [InlineData("[]", OpenAIFailureKind.MalformedResponse)]
    [InlineData("{\"choices\":{}}", OpenAIFailureKind.MalformedResponse)]
    [InlineData("{\"choices\":[{}]}", OpenAIFailureKind.MalformedResponse)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":{\"text\":\"not a string\"}}}]}", OpenAIFailureKind.MalformedResponse)]
    [InlineData("{\"choices\":[{\"finish_reason\":\"tool_calls\",\"message\":{\"content\":\"not final\"}}]}", OpenAIFailureKind.MalformedResponse)]
    public void UnusableResponsesAreClassifiedWithoutRetry(string body, object expectedKind)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var communicator = CreateCommunicator(handler);

        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));

        Assert.Equal((OpenAIFailureKind)expectedKind, error.FailureKind);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(body, error.Message);
    }

    [Fact]
    public void LeadingThinkIsRejectedByDefaultInsteadOfLeaked()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(CompletionBody("<think>analysis</think>final"))));
        using var communicator = CreateCommunicator(handler);
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal("ReasoningDetected", error.FailureKind.ToString());
    }

    [Theory]
    [InlineData("<think>analysis</think>final", "final")]
    [InlineData("  <reasoning>analysis</reasoning>\nfinal", "final")]
    [InlineData("<think>one</think><think>two</think>final", "final")]
    [InlineData("literal <think>quoted</think> text", "literal <think>quoted</think> text")]
    public void OptInLeadingThinkSeparationPreservesNonLeadingSourceMarkup(string content, string expected)
    {
        string? sent = null;
        using var handler = new FakeHandler(async (request, _) =>
        {
            sent = await request.Content!.ReadAsStringAsync();
            return JsonResponse(CompletionBody(content));
        });
        using var communicator = CreateCommunicator(handler, "\"_translator_reasoning\":\"strip_leading_think\"");
        Assert.Equal(expected, Translate(communicator));
        Assert.DoesNotContain("_translator_reasoning", sent!);
    }

    [Theory]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":null,\"reasoning_content\":\"analysis\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"<think>analysis</think>\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"<think>unclosed\"}}]}")]
    public void ReasoningOnlyOutputNeverBecomesTranslation(string body)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(body)));
        using var communicator = CreateCommunicator(handler, "\"_translator_reasoning\":\"strip_leading_think\"");
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal(OpenAIFailureKind.ReasoningOnly, error.FailureKind);
    }

    [Theory]
    [InlineData(400, "{\"error\":{\"code\":\"context_length_exceeded\"}}", OpenAIFailureKind.ContextLengthExceeded)]
    [InlineData(400, "{\"error\":{\"message\":\"maximum context length exceeded\"}}", OpenAIFailureKind.ContextLengthExceeded)]
    [InlineData(400, "{\"error\":{\"message\":\"unsupported sampling parameter\"}}", OpenAIFailureKind.InvalidRequest)]
    [InlineData(401, "not json", OpenAIFailureKind.Authentication)]
    [InlineData(403, "", OpenAIFailureKind.PermissionDenied)]
    [InlineData(404, "", OpenAIFailureKind.InvalidRequest)]
    [InlineData(422, "", OpenAIFailureKind.InvalidRequest)]
    public void PermanentHttpFailuresAreClassifiedWithoutBlindRetries(int status, string body, object expectedKind)
    {
        using var handler = new FakeHandler((_, _) =>
        {
            var response = JsonResponse(body, (HttpStatusCode)status);
            response.Headers.Add("x-request-id", "safe-request-id");
            return Task.FromResult(response);
        });
        using var communicator = CreateCommunicator(handler);
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal((OpenAIFailureKind)expectedKind, error.FailureKind);
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal("safe-request-id", error.RequestId);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task CancellationInterruptsRetryAfterWithoutSendingAgain()
    {
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler((_, _) =>
        {
            var response = JsonResponse("", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            received.TrySetResult();
            return Task.FromResult(response);
        });
        using var communicator = CreateCommunicator(handler);
        var running = Task.Run(() => Record.Exception(() => Translate(communicator)));
        await received.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Delay(50);
        communicator.Cancel();
        var error = await running.WaitAsync(TimeSpan.FromMilliseconds(500));
        Assert.IsType<OpenAICommunicationException>(error);
        Assert.Equal(OpenAIFailureKind.Cancelled, ((OpenAICommunicationException)error!).FailureKind);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(408, OpenAIFailureKind.Timeout)]
    [InlineData(429, OpenAIFailureKind.RateLimited)]
    [InlineData(500, OpenAIFailureKind.TransientServer)]
    [InlineData(502, OpenAIFailureKind.TransientServer)]
    [InlineData(503, OpenAIFailureKind.TransientServer)]
    [InlineData(504, OpenAIFailureKind.TransientServer)]
    public void TransientHttpRetriesAreBoundedAndUseExponentialBackoff(int status, object expectedKind)
    {
        var waits = new List<TimeSpan>();
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse("", (HttpStatusCode)status)));
        using var communicator = CreateCommunicator(handler, delay: (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal((OpenAIFailureKind)expectedKind, error.FailureKind);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(2, waits.Count);
        Assert.InRange(waits[0].TotalSeconds, 1, 1.25);
        Assert.InRange(waits[1].TotalSeconds, 2, 2.5);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RetryAfterDeltaOrDateIsAMinimumDelayBeforeSuccess(bool useDate)
    {
        var waits = new List<TimeSpan>();
        int calls = 0;
        using var handler = new FakeHandler((_, _) =>
        {
            if (++calls > 1) return Task.FromResult(JsonResponse(CompletionBody("final")));
            var response = JsonResponse("", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = useDate
                ? new System.Net.Http.Headers.RetryConditionHeaderValue(DateTimeOffset.UtcNow.AddSeconds(10))
                : new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return Task.FromResult(response);
        });
        using var communicator = CreateCommunicator(handler, delay: (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });
        Assert.Equal("final", Translate(communicator));
        Assert.Equal(2, handler.Calls);
        Assert.Single(waits);
        Assert.InRange(waits[0].TotalSeconds, useDate ? 9 : 10, 10.5);
    }

    [Fact]
    public void ExcessiveRetryAfterStopsInsteadOfRetryingEarlierThanRequested()
    {
        var waits = new List<TimeSpan>();
        using var handler = new FakeHandler((_, _) =>
        {
            var response = JsonResponse("", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromDays(100));
            return Task.FromResult(response);
        });
        using var communicator = CreateCommunicator(handler, delay: (delay, _) =>
        {
            waits.Add(delay);
            return Task.CompletedTask;
        });
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal(OpenAIFailureKind.RateLimited, error.FailureKind);
        Assert.Equal(1, handler.Calls);
        Assert.Empty(waits);
    }

    [Theory]
    [InlineData(200, false)]
    [InlineData(200, true)]
    [InlineData(400, false)]
    [InlineData(503, false)]
    public void EveryAttemptDisposesRequestAndResponseBeforeReturningOrWaiting(int status, bool malformed)
    {
        var requests = new List<HttpRequestMessage>();
        var responses = new List<TrackingContent>();
        using var handler = new FakeHandler((request, _) =>
        {
            requests.Add(request);
            var content = new TrackingContent(malformed ? "not JSON" : CompletionBody("final"));
            responses.Add(content);
            return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = content });
        });
        void AssertDisposed()
        {
            Assert.All(responses, response => Assert.True(response.Disposed));
            Assert.All(requests, request =>
            {
                Assert.Throws<ObjectDisposedException>(() => { _ = request.Content!.ReadAsStringAsync(); });
                Assert.Throws<ObjectDisposedException>(() => request.Method = HttpMethod.Get);
            });
        }
        using var communicator = CreateCommunicator(handler, delay: (_, _) =>
        {
            AssertDisposed();
            return Task.CompletedTask;
        });
        _ = Record.Exception(() => Translate(communicator));
        AssertDisposed();
        Assert.Equal(status == 503 ? 3 : 1, requests.Count);
        Assert.Equal(requests.Count, requests.Distinct().Count());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TransportAndTimeoutFailuresUseBoundedRetriesWithoutLeakingExceptionText(bool timeout)
    {
        int waits = 0;
        using var handler = new FakeHandler((_, _) => timeout
            ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("private server diagnostic"))
            : Task.FromException<HttpResponseMessage>(new HttpRequestException("private server diagnostic")));
        using var communicator = CreateCommunicator(handler, delay: (_, _) =>
        {
            waits++;
            return Task.CompletedTask;
        });
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal(timeout ? OpenAIFailureKind.Timeout : OpenAIFailureKind.Transport, error.FailureKind);
        Assert.Equal(3, handler.Calls);
        Assert.Equal(2, waits);
        Assert.DoesNotContain("private", error.ToString());
    }

    [Fact]
    public async Task CancellationInterruptsInFlightHttpWithoutRetry()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new FakeHandler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return JsonResponse(CompletionBody("unreachable"));
        });
        using var communicator = CreateCommunicator(handler);
        var running = Task.Run(() => Record.Exception(() => Translate(communicator)));
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        communicator.Cancel();
        var error = Assert.IsType<OpenAICommunicationException>(await running.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Equal(OpenAIFailureKind.Cancelled, error.FailureKind);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData(400, OpenAIFailureKind.InvalidRequest)]
    [InlineData(401, OpenAIFailureKind.Authentication)]
    [InlineData(403, OpenAIFailureKind.PermissionDenied)]
    public void HttpExceptionsWithPermanentStatusAreNotRetried(int status, object expectedKind)
    {
        using var handler = new FakeHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException("private diagnostic", null, (HttpStatusCode)status)));
        using var communicator = CreateCommunicator(handler, delay: (_, _) => Task.CompletedTask);
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal((OpenAIFailureKind)expectedKind, error.FailureKind);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("<think>outer<think>nested</think>left</think>final")]
    [InlineData("<think>outer<reasoning>nested</reasoning></think>final")]
    [InlineData("<think>analysis</think></think>final")]
    [InlineData("<think role=\"analysis\">analysis</think>final")]
    public void AmbiguousLeadingReasoningMarkupFailsClosedInsteadOfLeakingOrRemovingText(string content)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(CompletionBody(content))));
        using var communicator = CreateCommunicator(handler, "\"_translator_reasoning\":\"strip_leading_think\"");
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal(OpenAIFailureKind.MalformedResponse, error.FailureKind);
    }

    [Fact]
    public void LeadingThinkRecognitionIsCaseInsensitive()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(CompletionBody("<THINK>analysis</THINK>final"))));
        using var communicator = CreateCommunicator(handler, "\"_translator_reasoning\":\"strip_leading_think\"");
        Assert.Equal("final", Translate(communicator));
    }

    [Theory]
    [InlineData("\"temperature\":1,\"temperature\":2")]
    [InlineData("{invalid}")]
    [InlineData("\"_translator_reasoning\":\"silently_delete_anything\"")]
    public void InvalidExtendedParametersFailBeforeAnyHttpCall(string extended)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(CompletionBody("not sent"))));
        using var communicator = CreateCommunicator(handler, extended);
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal(OpenAIFailureKind.InvalidRequest, error.FailureKind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public void SeparateReasoningFieldsNeverReplaceFinalContentAndSpeedIsFinite()
    {
        var body = JObject.Parse(CompletionBody("final"));
        body["choices"]![0]!["message"]!["reasoning_content"] = "analysis";
        body["choices"]![0]!["message"]!["reasoning"] = "other analysis";
        body["usage"] = new JObject { ["completion_tokens"] = 12 };
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(body.ToString())));
        using var communicator = CreateCommunicator(handler);
        Assert.Equal("final", communicator.Translate(new PostDataBase(), [], [], "source", out double speed));
        Assert.True(double.IsFinite(speed));
        Assert.True(speed >= 0);
    }

    [Theory]
    [InlineData("{\"error\":{\"code\":400,\"type\":\"exceed_context_size_error\",\"message\":\"request exceeds the available context size\"}}")]
    [InlineData("{\"error\":{\"code\":\"invalid_request\",\"type\":\"context_length_exceeded\"}}")]
    public void CompatibleServerContextErrorsUseCodeAndTypeWithoutRetry(string body)
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(body, HttpStatusCode.BadRequest)));
        using var communicator = CreateCommunicator(handler);
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal(OpenAIFailureKind.ContextLengthExceeded, error.FailureKind);
        Assert.Equal(1, handler.Calls);
    }

    [Theory]
    [InlineData("insufficient_quota")]
    [InlineData("billing_hard_limit_reached")]
    public void PermanentQuotaErrorsDoNotConsumeTransientRetries(string code)
    {
        var body = new JObject { ["error"] = new JObject { ["code"] = code } }.ToString();
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(body, HttpStatusCode.TooManyRequests)));
        using var communicator = CreateCommunicator(handler, delay: (_, _) => Task.CompletedTask);
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal("QuotaExceeded", error.FailureKind.ToString());
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public void RequestMergeKeepsEntireSourceAndDialogueOrderWithoutChangingConfiguredBudget()
    {
        JObject? sent = null;
        using var handler = new FakeHandler(async (request, _) =>
        {
            sent = JObject.Parse(await request.Content!.ReadAsStringAsync());
            return JsonResponse(CompletionBody("final"));
        });
        using var communicator = CreateCommunicator(handler,
            "{\"max_completion_tokens\":128,\"temperature\":null,\"top_p\":null,\"stop\":null}");
        var parameters = new PostDataBase { max_tokens = 512, temperature = 0.5 };
        const string source = "source\n\"quoted\" \\control[17] 日本語";
        Assert.Equal("final", communicator.Translate(parameters, [new("system", "instruction")],
            [new("assistant", "history")], source, out double speed));
        Assert.NotNull(sent);
        Assert.Null(sent.Property("max_tokens"));
        Assert.Null(sent.Property("temperature"));
        Assert.Null(sent.Property("top_p"));
        Assert.Null(sent.Property("stop"));
        Assert.Equal(128, (int?)sent["max_completion_tokens"]);
        Assert.Equal("test-model", (string?)sent["model"]);
        Assert.Equal(new[] { "instruction", "history", source }, sent["messages"]!.Select(item => (string?)item["content"]));
        Assert.Equal(512, parameters.max_tokens);
        Assert.Equal(0, speed);
    }

    [Fact]
    public void CancelBeforeTranslationSendsNothing()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(CompletionBody("not sent"))));
        using var communicator = CreateCommunicator(handler);
        communicator.Cancel();
        var error = Assert.Throws<OpenAICommunicationException>(() => Translate(communicator));
        Assert.Equal(OpenAIFailureKind.Cancelled, error.FailureKind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task InjectedClientIsCallerOwnedUnlessOwnershipIsExplicitlyTransferred()
    {
        using var handler = new FakeHandler((_, _) => Task.FromResult(JsonResponse(CompletionBody("final"))));
        using var client = new HttpClient(handler);
        var communicator = new OpenAICommunicator(new Uri("https://example.invalid/"), "", "test-model", "", client);
        communicator.Dispose();
        communicator.Dispose();
        communicator.Cancel();
        using var response = await client.GetAsync("https://example.invalid/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class TrackingContent(string body) : StringContent(body, Encoding.UTF8, "application/json")
    {
        public bool Disposed { get; private set; }
        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private static string CompletionBody(string content) => new JObject
    {
        ["choices"] = new JArray(new JObject { ["finish_reason"] = "stop", ["message"] = new JObject { ["content"] = content } })
    }.ToString();

    private static OpenAICommunicator CreateCommunicator(HttpMessageHandler handler, string extended = "",
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        return new OpenAICommunicator(new Uri("https://example.invalid/v1/chat/completions"), "", "test-model", extended,
            new HttpClient(handler), delayAsync: delay, ownsClient: true);
    }

    private static string Translate(OpenAICommunicator communicator) =>
        communicator.Translate(new PostDataBase { max_tokens = 512 }, [], [], "source", out _);

    private static HttpResponseMessage JsonResponse(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return send(request, cancellationToken);
        }
    }

    [Fact]
    public void ExtendedParametersOmitNullDefaultsAndReplaceLegacyTokenLimit()
    {
        var request = new PostDataNormal
        {
            Base = new PostDataBase { max_tokens = 512, temperature = 0.5 },
            Expended = "\"max_completion_tokens\":256,\"temperature\":null,\"extra_body\":{\"enable_thinking\":false}"
        };

        var json = JObject.Parse(request.ToString());

        Assert.Null(json.Property("max_tokens"));
        Assert.Null(json.Property("temperature"));
        Assert.Equal(256, (int?)json["max_completion_tokens"]);
        Assert.False((bool)json["extra_body"]!["enable_thinking"]!);
    }
}
