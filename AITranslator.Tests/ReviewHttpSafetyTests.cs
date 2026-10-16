using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using AITranslator.Translator.Communicator;
using AITranslator.Translator.Models;
using AITranslator.Translator.PostData;
using Newtonsoft.Json.Linq;

namespace AITranslator.Tests;

public class ReviewHttpSafetyTests
{
    [Theory]
    [InlineData("\"messages\":null")]
    [InlineData("{\"messages\":[]}")]
    [InlineData("\"messages\":[{\"role\":\"user\",\"content\":\"replacement\"}]")]
    [InlineData("\"model\":null")]
    [InlineData("{\"model\":\"replacement-model\"}")]
    [InlineData("\"model\":\"test-model\"")]
    public void TransportOwnedExtensionsAreRejectedBeforeSendingSource(string extended)
    {
        using var handler = new ResponseHandler(CompletionBody("not sent"));
        using var communicator = CreateCommunicator(handler, extended);
        double speed = -1;

        var error = Assert.Throws<OpenAICommunicationException>(() =>
            communicator.Translate(new PostDataBase(), [new("system", "instruction")],
                [new("assistant", "history")], "private-source", out speed));

        Assert.Equal(OpenAIFailureKind.InvalidRequest, error.FailureKind);
        Assert.Equal(0, handler.Calls);
        Assert.Equal(0, speed);
        Assert.DoesNotContain("private-source", error.ToString());
        Assert.DoesNotContain("replacement", error.ToString());
    }

    [Theory]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\",\"finish_reason\":\"stop\",\"message\":{\"content\":\"partial\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"refusal\":\"declined\",\"refusal\":null,\"content\":\"unsafe\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"refusal\":\"declined\"},\"message\":{\"content\":\"unsafe\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"first\",\"content\":\"second\"}}]}")]
    [InlineData("{\"choices\":[{\"finish_reason\":\"length\"}],\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"partial\"}}]}")]
    public void DuplicateResponsePropertiesAreMalformedInsteadOfOverridingSafetyControls(string body)
    {
        using var handler = new ResponseHandler(body);
        using var communicator = CreateCommunicator(handler);

        var error = Assert.Throws<OpenAICommunicationException>(() =>
            communicator.Translate(new PostDataBase(), [], [], "source", out _));

        Assert.Equal(OpenAIFailureKind.MalformedResponse, error.FailureKind);
        Assert.Equal(HttpStatusCode.OK, error.StatusCode);
        Assert.Equal(1, handler.Calls);
        Assert.DoesNotContain(body, error.ToString());
    }

    [Theory]
    [InlineData("internal reasoning</think>final")]
    [InlineData("internal reasoning</reasoning>final")]
    [InlineData("</think>final")]
    [InlineData("  </reasoning>final")]
    [InlineData("internal reasoning</ThInK>final")]
    [InlineData("literal <think>unclosed")]
    [InlineData("literal <reasoning>unclosed")]
    [InlineData("literal <think>mismatched</reasoning> final")]
    [InlineData("literal <think>balanced</think> internal</think>final")]
    public void UnmatchedReasoningDelimitersFailClosedInBothModes(string content)
    {
        foreach (string mode in new[] { "", "\"_translator_reasoning\":\"strip_leading_think\"" })
        {
            using var handler = new ResponseHandler(CompletionBody(content));
            using var communicator = CreateCommunicator(handler, mode);

            var error = Assert.Throws<OpenAICommunicationException>(() =>
                communicator.Translate(new PostDataBase(), [], [], "source", out _));

            Assert.Equal(OpenAIFailureKind.MalformedResponse, error.FailureKind);
            Assert.Equal(1, handler.Calls);
            Assert.DoesNotContain(content, error.ToString());
        }
    }

    [Theory]
    [InlineData("literal <think>quoted</think> text")]
    [InlineData("literal <reasoning>quoted</reasoning> text")]
    [InlineData("literal <THINK>outer<reasoning>inner</reasoning></THINK> text")]
    [InlineData("literal <thinker>not a reasoning delimiter</thinker> text")]
    public void BalancedNonLeadingLiteralMarkupIsPreservedInBothModes(string content)
    {
        foreach (string mode in new[] { "", "\"_translator_reasoning\":\"strip_leading_think\"" })
        {
            using var handler = new ResponseHandler(CompletionBody(content));
            using var communicator = CreateCommunicator(handler, mode);

            Assert.Equal(content, communicator.Translate(new PostDataBase(), [], [], "source", out _));
            Assert.Equal(1, handler.Calls);
        }
    }

    [Theory]
    [InlineData(307)]
    [InlineData(308)]
    public async Task DefaultClientRejectsCrossOriginRedirectWithoutForwardingSourceOrHistory(int status)
    {
        await using var destination = new LoopbackServer(200, CompletionBody("forwarded"));
        await using var origin = new LoopbackServer(status, "", destination.Url);
        using var communicator = new OpenAICommunicator(origin.Url, "synthetic-test-key", "test-model", "");

        var error = await Task.Run(() => Record.Exception(() =>
            communicator.Translate(new PostDataBase(), [new("system", "private-instruction")],
                [new("assistant", "private-history")], "private-source", out _)))
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(1, origin.RequestCount);
        var sent = JObject.Parse(origin.RequestBody);
        Assert.Equal(new[] { "private-instruction", "private-history", "private-source" },
            sent["messages"]!.Select(item => (string?)item["content"]));
        Assert.Contains("Authorization: Bearer synthetic-test-key", origin.RequestHeaders);
        Assert.Equal(0, destination.RequestCount);
        var classified = Assert.IsType<OpenAICommunicationException>(error);
        Assert.Equal(OpenAIFailureKind.InvalidRequest, classified.FailureKind);
        Assert.Equal((HttpStatusCode)status, classified.StatusCode);
        Assert.Equal("redirect-test", classified.RequestId);
        Assert.DoesNotContain("private-", classified.ToString());
        Assert.DoesNotContain("synthetic-test-key", classified.ToString());
        Assert.DoesNotContain(destination.Url.ToString(), classified.ToString());
    }

    private static OpenAICommunicator CreateCommunicator(ResponseHandler handler, string extended = "") =>
        new(new Uri("https://example.invalid/v1/chat/completions"), "", "test-model", extended,
            new HttpClient(handler), delayAsync: (_, _) => Task.CompletedTask, ownsClient: true);

    private static string CompletionBody(string content) => new JObject
    {
        ["choices"] = new JArray(new JObject
        {
            ["finish_reason"] = "stop",
            ["message"] = new JObject { ["content"] = content }
        })
    }.ToString();

    // Real sockets exercise the production constructor and HttpClientHandler redirect policy.
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _shutdown = new(TimeSpan.FromSeconds(10));
        private readonly Task _serving;
        private int _requestCount;
        public Uri Url { get; }
        public int RequestCount => Volatile.Read(ref _requestCount);
        public string RequestHeaders { get; private set; } = "";
        public string RequestBody { get; private set; } = "";

        public LoopbackServer(int status, string body, Uri? location = null)
        {
            _listener.Start();
            Url = new Uri($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/v1/chat/completions");
            string response = $"HTTP/1.1 {status} Test\r\nx-request-id: redirect-test\r\n" +
                (location is null ? "" : $"Location: {location}\r\n") +
                $"Content-Type: application/json\r\nContent-Length: {Encoding.UTF8.GetByteCount(body)}\r\n" +
                $"Connection: close\r\n\r\n{body}";
            _serving = ServeAsync(response);
        }

        private async Task ServeAsync(string response)
        {
            CancellationToken token = _shutdown.Token;
            using var connection = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            using var stream = connection.GetStream();
            using var reader = new StreamReader(stream, Encoding.Latin1, false, 1024, leaveOpen: true);
            var headers = new List<string>();
            while (true)
            {
                string line = await reader.ReadLineAsync(token).ConfigureAwait(false)
                    ?? throw new IOException("Incomplete loopback HTTP headers.");
                if (line.Length == 0) break;
                headers.Add(line);
            }
            RequestHeaders = string.Join("\r\n", headers);
            string? length = headers.FirstOrDefault(line => line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase));
            int count = length is null ? 0 : int.Parse(length.Split(':', 2)[1], System.Globalization.CultureInfo.InvariantCulture);
            var buffer = new char[count];
            int read = await reader.ReadBlockAsync(buffer.AsMemory(), token).ConfigureAwait(false);
            if (read != count) throw new IOException("Incomplete loopback HTTP body.");
            // Latin1 is byte-preserving, so Content-Length remains a byte count.
            RequestBody = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(buffer));
            Interlocked.Increment(ref _requestCount);
            await stream.WriteAsync(Encoding.UTF8.GetBytes(response), token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            _listener.Stop();
            try { await _serving; }
            catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
            catch (SocketException) when (_shutdown.IsCancellationRequested) { }
            _shutdown.Dispose();
        }
    }

    private sealed class ResponseHandler(string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
