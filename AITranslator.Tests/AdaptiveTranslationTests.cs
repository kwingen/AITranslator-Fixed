using AITranslator.Translator.Communicator;
using AITranslator.Translator.Translation;
using AITranslator.Translator.TranslateData;

namespace AITranslator.Tests;

public class AdaptiveTranslationTests
{
    [Fact]
    public void ContextOverflowDropsOldestHistoryBeforeRetrying()
    {
        using var fixture = new AdaptiveCheckpointFixture(TranslateDataType.KV, "source");
        var transport = new AdaptiveCheckpointCommunicator((_, history) => history.Length > 0
            ? throw new OpenAICommunicationException(OpenAIFailureKind.ContextLengthExceeded)
            : "translated");
        var translator = fixture.Create(transport);
        translator._history.Enqueue(new("user", "old input"));
        translator._history.Enqueue(new("assistant", "old output"));

        Assert.Equal("translated", translator.TryTranslate("source", "prompt:", true, TryTranslateType.Single));
        Assert.Equal(new[] { 2, 0 }, transport.HistoryCounts);
        Assert.All(transport.Inputs, input => Assert.Equal("prompt:source", input));
    }

    [Theory]
    [InlineData(TranslateDataType.KV, "ContextLengthExceeded")]
    [InlineData(TranslateDataType.KV, "OutputTruncated")]
    [InlineData(TranslateDataType.Tpp, "ContextLengthExceeded")]
    [InlineData(TranslateDataType.Tpp, "OutputTruncated")]
    [InlineData(TranslateDataType.Txt, "ContextLengthExceeded")]
    [InlineData(TranslateDataType.Txt, "OutputTruncated")]
    public void FallbackCheckpointsFirstVerifiedEntryBeforeSecondFailsAndResumeSkipsIt(TranslateDataType type, string kind)
    {
        using var fixture = new AdaptiveCheckpointFixture(type, "a", "b");
        var failure = Enum.Parse<OpenAIFailureKind>(kind);
        Dictionary<string, string>? checkpointAtSecondRequest = null;
        var transport = new AdaptiveCheckpointCommunicator((input, _) =>
        {
            if (input.Contains('\n')) throw new OpenAICommunicationException(failure);
            if (input == "a") return "A";
            checkpointAtSecondRequest = fixture.ReadSuccessfulCheckpoint();
            throw new OpenAICommunicationException(OpenAIFailureKind.Authentication);
        });
        var translator = fixture.Create(transport);

        var error = Assert.Throws<OpenAICommunicationException>(() => translator.Translate());

        Assert.Equal(OpenAIFailureKind.Authentication, error.FailureKind);
        Assert.NotNull(checkpointAtSecondRequest);
        Assert.Equal(new Dictionary<string, string> { [fixture.Key(0)] = "A" }, checkpointAtSecondRequest);
        Assert.Equal(checkpointAtSecondRequest, fixture.ReadSuccessfulCheckpoint());
        Assert.Equal(new[] { "a\nb", "a", "b" }, transport.Sources);
        Assert.Equal(new int[] { 102, 101, 101 }, transport.TokenLimits);
        Assert.Equal(50, fixture.Task.Progress);

        var resumedTransport = new AdaptiveCheckpointCommunicator((input, _) => input.ToUpperInvariant());
        var resumed = fixture.Create(resumedTransport);
        Assert.Equal("A", fixture.Successful(resumed)[fixture.Key(0)]);
        resumed.Translate();

        Assert.Equal(new[] { "b" }, resumedTransport.Sources);
        Assert.Equal(new Dictionary<string, string> { [fixture.Key(0)] = "A", [fixture.Key(1)] = "B" }, fixture.ReadSuccessfulCheckpoint());
        Assert.Empty(fixture.Failed(resumed));
        Assert.Equal(100, fixture.Task.Progress);
    }

    [Theory]
    [InlineData("ContextLengthExceeded", true)]
    [InlineData("ContextLengthExceeded", false)]
    [InlineData("OutputTruncated", true)]
    [InlineData("OutputTruncated", false)]
    public void PlainTextFallbackCannotCombineOpposingChildCountErrorsIntoMisalignedRecords(string kind, bool firstChildOverfull)
    {
        using var fixture = new AdaptiveCheckpointFixture(TranslateDataType.Txt, "a", "b", "c", "d");
        var failure = Enum.Parse<OpenAIFailureKind>(kind);
        var transport = new AdaptiveCheckpointCommunicator((input, _) => input switch
        {
            "a\nb\nc\nd" => throw new OpenAICommunicationException(failure),
            // Recursive 2+2 requests used to accept 3+1 (or 1+3) lines as a valid total of four.
            "a\nb" => firstChildOverfull ? "A\nB\nEXTRA" : "A",
            "c\nd" => firstChildOverfull ? "D" : "EXTRA\nC\nD",
            _ => input.ToUpperInvariant()
        });
        var translator = fixture.Create(transport);

        translator.Translate();

        var expected = new Dictionary<string, string> { ["0"] = "A", ["1"] = "B", ["2"] = "C", ["3"] = "D" };
        Assert.Equal(expected, fixture.ReadSuccessfulCheckpoint());
        var reloaded = fixture.Create(new AdaptiveCheckpointCommunicator((_, _) => throw new InvalidOperationException("No entries should remain.")));
        Assert.Equal(expected, fixture.Successful(reloaded));
        reloaded.Translate();
        Assert.Empty(fixture.Failed(reloaded));
        Assert.Equal(new[] { "a\nb\nc\nd", "a", "b", "c", "d" }, transport.Sources);
        Assert.Equal(new int[] { 102, 101, 101, 101, 101 }, transport.TokenLimits);
    }

    // Characterization guards for the unchanged verification, history and terminal-failure paths.
    [Theory]
    [InlineData(TranslateDataType.KV, "ContextLengthExceeded")]
    [InlineData(TranslateDataType.KV, "OutputTruncated")]
    [InlineData(TranslateDataType.Tpp, "ContextLengthExceeded")]
    [InlineData(TranslateDataType.Tpp, "OutputTruncated")]
    [InlineData(TranslateDataType.Txt, "ContextLengthExceeded")]
    [InlineData(TranslateDataType.Txt, "OutputTruncated")]
    public void FallbackVerifiesRetryBeforeCheckpointAndNextEntry(TranslateDataType type, string kind)
    {
        using var fixture = new AdaptiveCheckpointFixture(type, "a", "b");
        var failure = Enum.Parse<OpenAIFailureKind>(kind);
        int firstEntryAttempts = 0;
        Dictionary<string, string>? checkpointBeforeRetry = null;
        Dictionary<string, string>? checkpointBeforeNextEntry = null;
        var transport = new AdaptiveCheckpointCommunicator((input, _) =>
        {
            if (input.Contains('\n')) throw new OpenAICommunicationException(failure);
            if (input == "a")
            {
                if (++firstEntryAttempts == 1) return "invalid";
                checkpointBeforeRetry = fixture.ReadSuccessfulCheckpoint();
                return "A";
            }
            checkpointBeforeNextEntry = fixture.ReadSuccessfulCheckpoint();
            return "B";
        });
        var translator = fixture.Create(transport);

        translator.Translate();

        Assert.NotNull(checkpointBeforeRetry);
        Assert.Empty(checkpointBeforeRetry);
        Assert.NotNull(checkpointBeforeNextEntry);
        Assert.Equal(new Dictionary<string, string> { [fixture.Key(0)] = "A" }, checkpointBeforeNextEntry);
        Assert.Equal(new Dictionary<string, string> { [fixture.Key(0)] = "A", [fixture.Key(1)] = "B" }, fixture.ReadSuccessfulCheckpoint());
        Assert.Equal(new[] { "a\nb", "a", "a", "b" }, transport.Sources);
        Assert.Equal(new int[] { 102, 101, 103, 101 }, transport.TokenLimits);
        Assert.Empty(fixture.Failed(translator));
        Assert.DoesNotContain(translator._history, message => message.content == "invalid");
    }

    [Theory]
    [InlineData(TranslateDataType.KV, "ContextLengthExceeded")]
    [InlineData(TranslateDataType.KV, "OutputTruncated")]
    [InlineData(TranslateDataType.Tpp, "ContextLengthExceeded")]
    [InlineData(TranslateDataType.Tpp, "OutputTruncated")]
    [InlineData(TranslateDataType.Txt, "ContextLengthExceeded")]
    [InlineData(TranslateDataType.Txt, "OutputTruncated")]
    public void SingleEntryOverLimitPropagatesKnownFailureWithoutSavingOrBlindRetry(TranslateDataType type, string kind)
    {
        using var fixture = new AdaptiveCheckpointFixture(type, "overlimit");
        var failure = Enum.Parse<OpenAIFailureKind>(kind);
        var expected = new OpenAICommunicationException(failure);
        var transport = new AdaptiveCheckpointCommunicator((_, _) => throw expected);
        var translator = fixture.Create(transport);
        translator._history.Enqueue(new("user", "old source"));
        translator._history.Enqueue(new("assistant", "old output"));
        translator._history.Enqueue(new("user", "new source"));
        translator._history.Enqueue(new("assistant", "new output"));

        var error = Assert.Throws<OpenAICommunicationException>(() => translator.Translate());

        Assert.Same(expected, error);
        Assert.IsAssignableFrom<AITranslator.Exceptions.KnownException>(error);
        Assert.Contains(kind, error.Message);
        Assert.Equal(failure == OpenAIFailureKind.ContextLengthExceeded ? new[] { 4, 2, 0 } : new[] { 4 }, transport.HistoryCounts);
        Assert.All(transport.Inputs, input => Assert.Equal("overlimit", input));
        Assert.All(transport.TokenLimits, limit => Assert.Equal(101, limit));
        Assert.Empty(fixture.ReadSuccessfulCheckpoint());
        Assert.Empty(fixture.Successful(translator));
        Assert.Empty(fixture.Failed(translator));
        Assert.Equal(0, fixture.Task.Progress);
    }

    [Theory]
    [InlineData(TranslateDataType.KV, "Authentication")]
    [InlineData(TranslateDataType.KV, "RateLimited")]
    [InlineData(TranslateDataType.KV, "Cancelled")]
    [InlineData(TranslateDataType.Tpp, "Authentication")]
    [InlineData(TranslateDataType.Tpp, "RateLimited")]
    [InlineData(TranslateDataType.Tpp, "Cancelled")]
    [InlineData(TranslateDataType.Txt, "Authentication")]
    [InlineData(TranslateDataType.Txt, "RateLimited")]
    [InlineData(TranslateDataType.Txt, "Cancelled")]
    public void OtherBatchFailuresDoNotTriggerSmallerRequests(TranslateDataType type, string kind)
    {
        using var fixture = new AdaptiveCheckpointFixture(type, "a", "b");
        var expected = new OpenAICommunicationException(Enum.Parse<OpenAIFailureKind>(kind));
        var transport = new AdaptiveCheckpointCommunicator((_, _) => throw expected);
        var translator = fixture.Create(transport);

        var error = Assert.Throws<OpenAICommunicationException>(() => translator.Translate());

        Assert.Same(expected, error);
        Assert.Equal(new[] { "a\nb" }, transport.Sources);
        Assert.Equal(new int[] { 102 }, transport.TokenLimits);
        Assert.Empty(fixture.ReadSuccessfulCheckpoint());
        Assert.Empty(fixture.Successful(translator));
        Assert.Empty(fixture.Failed(translator));
    }
}
