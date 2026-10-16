using AITranslator.Translator.Models;
using AITranslator.Translator.PostData;
using Newtonsoft.Json;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace AITranslator.Translator.Communicator;

internal class OpenAICommunicator : ICommunicator
{
    private const int MaxAttempts = 3;
    private readonly HttpClient _client;
    private readonly bool _ownsClient;
    private readonly Uri _url;
    private readonly string _apiKey;
    private readonly string _modelName;
    private readonly string _expendedParams;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<TimeSpan, CancellationToken, Task> _delayAsync;
    private int _disposed;

    public OpenAICommunicator(Uri url, string api_key, string modelName, string expendedParams)
        : this(url, api_key, modelName, expendedParams,
            // Redirects can forward source/history even when Authorization is stripped.
            new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            { Timeout = TimeSpan.FromMinutes(10) }, ownsClient: true) { }

    // Injected clients remain caller-owned unless explicitly transferred.
    internal OpenAICommunicator(Uri url, string api_key, string modelName, string expendedParams,
        HttpClient client, Func<TimeSpan, CancellationToken, Task>? delayAsync = null, bool ownsClient = false)
    {
        _url = url;
        _apiKey = api_key;
        _modelName = modelName;
        _expendedParams = expendedParams;
        _client = client;
        _ownsClient = ownsClient;
        _delayAsync = delayAsync ?? Task.Delay;
    }

    public string Translate(PostDataBase postData, ExampleDialogue[] headers, ExampleDialogue[] histories,
        string inputText, out double speed)
    {
        speed = 0;
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        CancellationToken token = _cts.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            List<ExampleDialogue> messages = [.. headers, .. histories, new("user", inputText)];
            postData.messages = messages.ToArray();
            postData.model = _modelName;
            var normal = new PostDataNormal { Base = postData, Expended = _expendedParams };
            string json;
            try { json = normal.ToString(); }
            catch (JsonException) { throw new OpenAICommunicationException(OpenAIFailureKind.InvalidRequest); }
            var result = TranslateAsync(json, normal.StripLeadingThink, token).GetAwaiter().GetResult();
            speed = result.Speed;
            return result.Content;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw new OpenAICommunicationException(OpenAIFailureKind.Cancelled);
        }
    }

    private async Task<(string Content, double Speed)> TranslateAsync(string json, bool stripLeadingThink,
        CancellationToken token)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            token.ThrowIfCancellationRequested();
            OpenAICommunicationException failure;
            TimeSpan? retryAfter = null;
            try
            {
                // Dispose both request (including its content) and response before waiting.
                using var request = new HttpRequestMessage(HttpMethod.Post, _url)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };
                if (!string.IsNullOrWhiteSpace(_apiKey))
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                var watch = Stopwatch.StartNew();
                using var response = await _client.SendAsync(request, token).ConfigureAwait(false);
                string body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
                watch.Stop();
                string? requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
                if (response.IsSuccessStatusCode)
                {
                    var completion = OpenAICompletionParser.Parse(body, response.StatusCode, requestId, stripLeadingThink);
                    double speed = completion.CompletionTokens.HasValue && watch.Elapsed.TotalSeconds > 0
                        ? completion.CompletionTokens.Value / watch.Elapsed.TotalSeconds : 0;
                    return (completion.Content, speed);
                }
                failure = OpenAIHttpFailure.Classify(response.StatusCode, body, requestId);
                if (!OpenAIHttpFailure.IsTransient(response.StatusCode) || failure.FailureKind == OpenAIFailureKind.QuotaExceeded)
                    throw failure;
                retryAfter = response.Headers.RetryAfter?.Delta;
                if (response.Headers.RetryAfter?.Date is DateTimeOffset date)
                    retryAfter = date - DateTimeOffset.UtcNow;
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            {
                failure = new OpenAICommunicationException(OpenAIFailureKind.Timeout);
            }
            catch (HttpRequestException error)
            {
                failure = error.StatusCode.HasValue
                    ? OpenAIHttpFailure.Classify(error.StatusCode.Value, string.Empty, null)
                    : new OpenAICommunicationException(OpenAIFailureKind.Transport);
                if (error.StatusCode.HasValue && !OpenAIHttpFailure.IsTransient(error.StatusCode.Value)) throw failure;
            }
            if (attempt == MaxAttempts) throw failure;
            TimeSpan backoff = TimeSpan.FromSeconds(Math.Pow(2, attempt - 1) * (1 + Random.Shared.NextDouble() * 0.25));
            TimeSpan wait = retryAfter.HasValue && retryAfter.Value > backoff ? retryAfter.Value : backoff;
            // Do not overflow Task.Delay or ignore a long server throttle by retrying early.
            // Surface the classified failure so the user can resume after the server's window.
            if (wait > TimeSpan.FromMinutes(10)) throw failure;
            await _delayAsync(wait, token).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Unreachable retry state.");
    }

    public void Cancel()
    {
        if (Volatile.Read(ref _disposed) == 0) _cts.Cancel();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cts.Cancel();
        _cts.Dispose();
        if (_ownsClient) _client.Dispose();
    }
}
