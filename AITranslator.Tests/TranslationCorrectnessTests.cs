using AITranslator.Translator.Communicator;
using AITranslator.Translator.Models;
using AITranslator.Translator.Persistent;
using AITranslator.Translator.PostData;
using AITranslator.Translator.Translation;
using AITranslator.Translator.TranslateData;
using AITranslator.View.Models;
using Newtonsoft.Json;
using System.IO;
using System.Windows.Threading;

namespace AITranslator.Tests;

[CollectionDefinition("Translation correctness", DisableParallelization = true)]
public sealed class TranslationCorrectnessCollection;

[Collection("Translation correctness")]
public sealed class TranslationCorrectnessTests
{
    [Theory]
    [InlineData(TranslateDataType.KV)]
    [InlineData(TranslateDataType.Tpp)]
    public void RetranslationAcceptsAndPersistsLatestValidOutput(TranslateDataType type)
    {
        using var fixture = new TranslationFixture(type);
        var communicator = new QueuedCommunicator("bad", "good");
        var translator = fixture.CreateTranslator(communicator);

        translator.Translate();

        Assert.Equal(2, communicator.Inputs.Count);
        Assert.True(fixture.Successful(translator).ContainsKey(fixture.SourceKey), "The valid retry must be recorded as successful.");
        Assert.Equal("good", fixture.Successful(translator)[fixture.SourceKey]);
        Assert.Empty(fixture.Failed(translator));
        Assert.Equal("good", translator._history.Last().content);
        translator.TranslateData.ReloadData();
        Assert.Equal("good", fixture.Successful(translator)[fixture.SourceKey]);
    }

    [Theory]
    [InlineData(TranslateDataType.KV)]
    [InlineData(TranslateDataType.Tpp)]
    public void RetranslationPersistsLatestRejectedOutput(TranslateDataType type)
    {
        using var fixture = new TranslationFixture(type);
        var communicator = new QueuedCommunicator("bad", "still invalid");
        var translator = fixture.CreateTranslator(communicator);

        translator.Translate();

        Assert.Equal("still invalid", fixture.Failed(translator)[fixture.SourceKey]);
        Assert.Empty(fixture.Successful(translator));
        Assert.Empty(translator._history);
        translator.TranslateData.ReloadData();
        Assert.Equal("still invalid", fixture.Failed(translator)[fixture.SourceKey]);
    }

    [Fact]
    public void TppBatchFallbackRetainsStableSourceKeyThroughReloadAndMerge()
    {
        using var fixture = new TranslationFixture(TranslateDataType.Tpp);
        var source = new Dictionary<string, Dictionary<string, string?>>
        {
            ["scene.csv"] = new() { ["source"] = null, ["other"] = null }
        };
        CsvPersister.SaveToFolder(fixture.PathFor(GenerateFileType.Source), source);
        fixture.WriteJson(GenerateFileType.Cleaned, new Dictionary<string, Dictionary<string, string?>>
        {
            ["scene.csv"] = new() { ["source"] = "cleaned source", ["other"] = "other" }
        });
        var communicator = new QueuedCommunicator("wrong batch size", "bad", "still invalid", "good");
        var translator = (TppTranslator)fixture.CreateTranslator(communicator);

        var error = Record.Exception(() => translator.Translate());

        Assert.True(translator.Data.Dic_Failed!.ContainsKey(fixture.SourceKey), "Fallback failures must retain the filename||source key, not the cleaned text.");
        Assert.Null(error);
        Assert.Equal(4, communicator.Inputs.Count);
        Assert.Equal("still invalid", translator.Data.Dic_Failed[fixture.SourceKey]);
        translator.Data.ReloadData();
        Assert.Equal("still invalid", translator.Data.Dic_Failed[fixture.SourceKey]);
        translator.Data.GetUntranslatedData();
        Assert.Empty(translator.Data.Dic_NotTranslated);
        translator.MergeData();
        var merged = CsvPersister.LoadMergeDicFromFolder(fixture.PathFor(GenerateFileType.Merged));
        Assert.Equal("still invalid", merged[fixture.SourceKey]);
        Assert.Equal("good", merged["scene.csv||other"]);
    }

    [Fact]
    public void SrtCleaningPreservesOriginalCueIdsAndTimestamps()
    {
        using var fixture = new TranslationFixture(TranslateDataType.Srt);
        fixture.WriteTemplate(TemplateType.Clean, "clean", "Strs.Remove(\"second\"); return Strs;");
        string script = PublicParams.GetTemplateFilePath(fixture.Task.TemplateConfigParams.TemplateDic!, TemplateType.Clean, "clean");
        var original = SrtPersister.Load(fixture.PathFor(GenerateFileType.Source));

        SrtTranslateData.Clear(fixture.Task.DicName, script, CancellationToken.None);

        var cleaned = SrtPersister.Load(fixture.PathFor(GenerateFileType.Cleaned));
        Assert.Equal(new[] { 10 }, cleaned.Keys);
        Assert.Equal(original[10].Time, cleaned[10].Time);
        Assert.Equal(original[10].Text, cleaned[10].Text);
        var sourceAfterCleaning = SrtPersister.Load(fixture.PathFor(GenerateFileType.Source));
        Assert.Equal(new[] { 10, 20 }, sourceAfterCleaning.Keys);
        Assert.Equal("second", sourceAfterCleaning[20].Text);
    }

    [Fact]
    public void SrtTranslationAndMergePreserveOriginalCueIdsAndTimestamps()
    {
        using var fixture = new TranslationFixture(TranslateDataType.Srt);
        fixture.CleanSrt();
        var original = SrtPersister.Load(fixture.PathFor(GenerateFileType.Source));
        var communicator = new QueuedCommunicator("good", "good");
        var translator = (SrtTranslator)fixture.CreateTranslator(communicator);

        translator.Translate();
        translator.MergeData();

        var merged = SrtPersister.Load(fixture.PathFor(GenerateFileType.Merged));
        Assert.Equal(original.Keys, merged.Keys);
        Assert.All(original, cue => Assert.Equal(cue.Value.Time, merged[cue.Key].Time));
        Assert.All(merged.Values, cue => Assert.Equal("good", cue.Text));
        Assert.Equal(TaskState.Completed, fixture.Task.State);
    }

    [Fact]
    public void SrtMergeRejectsMissingTranslationWithoutOverwritingPreviousOutput()
    {
        using var fixture = new TranslationFixture(TranslateDataType.Srt);
        fixture.CleanSrt();
        var original = SrtPersister.Load(fixture.PathFor(GenerateFileType.Source));
        SrtPersister.Save(new Dictionary<int, SrtData> { [10] = new([original[10].Time, "good"]) }, fixture.PathFor(GenerateFileType.Successful));
        File.WriteAllText(fixture.PathFor(GenerateFileType.Merged), "previous output");
        var translator = fixture.CreateTranslator(new QueuedCommunicator());
        fixture.Task.State = TaskState.WaitMerge;

        var error = Assert.Throws<AITranslator.Exceptions.KnownException>(() => translator.MergeData());

        Assert.Contains("20", error.Message);
        Assert.Equal(TaskState.WaitMerge, fixture.Task.State);
        Assert.Equal("previous output", File.ReadAllText(fixture.PathFor(GenerateFileType.Merged)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SrtMergeKeepsOriginalCuesExcludedFromTranslation(bool excludeAll)
    {
        using var fixture = new TranslationFixture(TranslateDataType.Srt);
        fixture.WriteTemplate(TemplateType.Clean, "clean", excludeAll ? "Strs.Clear(); return Strs;" : "Strs.Remove(\"second\"); return Strs;");
        fixture.CleanSrt();
        var original = SrtPersister.Load(fixture.PathFor(GenerateFileType.Source));
        var translator = fixture.CreateTranslator(new QueuedCommunicator("good"));
        translator.Translate();

        var error = Record.Exception(() => translator.MergeData());

        Assert.Null(error);
        var merged = SrtPersister.Load(fixture.PathFor(GenerateFileType.Merged));
        Assert.Equal(original.Keys, merged.Keys);
        Assert.All(original, cue => Assert.Equal(cue.Value.Time, merged[cue.Key].Time));
        Assert.Equal(excludeAll ? "first" : "good", merged[10].Text);
        Assert.Equal("second", merged[20].Text);
        Assert.Equal(TaskState.Completed, fixture.Task.State);
    }

    [Theory]
    [InlineData(GenerateFileType.Successful)]
    [InlineData(GenerateFileType.Failed)]
    public void SrtMergeUsesSourceTimestampsInsteadOfEditedTranslationTimestamps(GenerateFileType recordsType)
    {
        using var fixture = new TranslationFixture(TranslateDataType.Srt);
        fixture.CleanSrt();
        var original = SrtPersister.Load(fixture.PathFor(GenerateFileType.Source));
        var records = original.ToDictionary(cue => cue.Key, cue => new SrtData(["00:01:00,000 --> 00:02:00,000", "good"]));
        SrtPersister.Save(records, fixture.PathFor(recordsType));
        var translator = fixture.CreateTranslator(new QueuedCommunicator());

        translator.MergeData();

        var merged = SrtPersister.Load(fixture.PathFor(GenerateFileType.Merged));
        Assert.Equal(original.Keys, merged.Keys);
        Assert.All(original, cue => Assert.Equal(cue.Value.Time, merged[cue.Key].Time));
        Assert.All(merged.Values, cue => Assert.Equal("good", cue.Text));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SrtMergeRejectsLegacyCleanedCueMapping(bool collidingId)
    {
        using var fixture = new TranslationFixture(TranslateDataType.Srt);
        var original = SrtPersister.Load(fixture.PathFor(GenerateFileType.Source));
        int legacyId = collidingId ? 10 : 1;
        var legacy = new Dictionary<int, SrtData> { [legacyId] = original[20] };
        SrtPersister.Save(legacy, fixture.PathFor(GenerateFileType.Cleaned));
        SrtPersister.Save(legacy, fixture.PathFor(GenerateFileType.Successful));
        File.WriteAllText(fixture.PathFor(GenerateFileType.Merged), "previous output");
        var translator = fixture.CreateTranslator(new QueuedCommunicator());

        var error = Assert.Throws<AITranslator.Exceptions.KnownException>(() => translator.MergeData());

        Assert.Contains(legacyId.ToString(), error.Message);
        Assert.NotEqual(TaskState.Completed, fixture.Task.State);
        Assert.Equal("previous output", File.ReadAllText(fixture.PathFor(GenerateFileType.Merged)));
    }

    [Theory]
    [InlineData(GenerateFileType.Successful)]
    [InlineData(GenerateFileType.Failed)]
    public void SrtMergeRejectsForeignTranslationCueIds(GenerateFileType recordsType)
    {
        using var fixture = new TranslationFixture(TranslateDataType.Srt);
        fixture.CleanSrt();
        var original = SrtPersister.Load(fixture.PathFor(GenerateFileType.Source));
        var successful = original.ToDictionary(cue => cue.Key, cue => new SrtData([cue.Value.Time, "good"]));
        SrtPersister.Save(successful, fixture.PathFor(GenerateFileType.Successful));
        var records = recordsType == GenerateFileType.Successful ? successful : new Dictionary<int, SrtData>();
        records[99] = new([original[20].Time, "foreign translation"]);
        SrtPersister.Save(records, fixture.PathFor(recordsType));
        File.WriteAllText(fixture.PathFor(GenerateFileType.Merged), "previous output");
        var translator = fixture.CreateTranslator(new QueuedCommunicator());

        var error = Assert.Throws<AITranslator.Exceptions.KnownException>(() => translator.MergeData());

        Assert.Contains("99", error.Message);
        Assert.NotEqual(TaskState.Completed, fixture.Task.State);
        Assert.Equal("previous output", File.ReadAllText(fixture.PathFor(GenerateFileType.Merged)));
    }

    [Theory]
    [InlineData(TranslateDataType.KV)]
    [InlineData(TranslateDataType.Srt)]
    public void MergeCreatesMissingOutputDirectory(TranslateDataType type)
    {
        using var fixture = new TranslationFixture(type);
        if (type == TranslateDataType.Srt)
            fixture.CleanSrt();
        var translator = fixture.CreateTranslator(new QueuedCommunicator("good", "good"));
        translator.Translate();
        string outputDirectory = Path.GetDirectoryName(fixture.PathFor(GenerateFileType.Merged))!;
        Directory.Delete(outputDirectory);

        var error = Record.Exception(() => translator.MergeData());

        Assert.Null(error);
        Assert.True(File.Exists(fixture.PathFor(GenerateFileType.Merged)));
        Assert.Equal(TaskState.Completed, fixture.Task.State);
    }

    private sealed class TranslationFixture : IDisposable
    {
        private readonly string id = "translation-tests-" + Guid.NewGuid().ToString("N");
        private readonly string previousDirectory = Environment.CurrentDirectory;
        private readonly Dispatcher? previousDispatcher;
        private readonly string root;
        internal TranslationTask Task { get; }
        internal string SourceKey => Task.TranslateType == TranslateDataType.Tpp ? "scene.csv||source" : "source";

        internal TranslationFixture(TranslateDataType type)
        {
            root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), id);
            Directory.CreateDirectory(root);
            Environment.CurrentDirectory = root;
            previousDispatcher = ViewModelManager.ViewModel.Dispatcher;
            ViewModelManager.ViewModel.Dispatcher = Dispatcher.CurrentDispatcher;
            Task = new TranslationTask
            {
                DicName = id,
                FileName = type switch { TranslateDataType.Tpp => "translated", TranslateDataType.Srt => "translated.srt", _ => "translated.json" },
                TranslateType = type,
                TemplateConfigParams = new ConfigSave_DefaultTemplate
                {
                    TemplateDic = id,
                    PromptTemplate = "prompt",
                    ReplaceTemplate = "replace",
                    VerificationTemplate = "verify",
                    CleanTemplate = "clean",
                    HistoryCount = 2,
                    TranslatePrams_FirstSingle = Params(),
                    TranslatePrams_FirstMult = Params(),
                    TranslatePrams_Retry = Params()
                }
            };
            WriteTemplate(TemplateType.Prompt, "prompt", "[{\"role\":\"user\",\"content\":\"\"}]");
            WriteTemplate(TemplateType.Replace, "replace", "{}");
            WriteTemplate(TemplateType.Verification, "verify", "return (Translated == \"good\", \"invalid output\");");
            WriteTemplate(TemplateType.Clean, "clean", "return Strs;");
            Directory.CreateDirectory(PublicParams.GetDicName(id));
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor(GenerateFileType.Merged))!);
            if (type == TranslateDataType.KV)
            {
                var source = new Dictionary<string, string> { [SourceKey] = "source" };
                WriteJson(GenerateFileType.Source, source);
                WriteJson(GenerateFileType.Cleaned, source);
            }
            else if (type == TranslateDataType.Tpp)
            {
                var source = new Dictionary<string, Dictionary<string, string?>>
                {
                    ["scene.csv"] = new() { ["source"] = "source" }
                };
                CsvPersister.SaveToFolder(PathFor(GenerateFileType.Source), source);
                WriteJson(GenerateFileType.Cleaned, source);
            }
            else if (type == TranslateDataType.Srt)
            {
                SrtPersister.Save(new Dictionary<int, SrtData>
                {
                    [10] = new(["00:00:01,250 --> 00:00:02,750", "first"]),
                    [20] = new(["00:00:04,000 --> 00:00:06,500", "second"])
                }, PathFor(GenerateFileType.Source));
            }
        }

        internal void CleanSrt()
        {
            string script = PublicParams.GetTemplateFilePath(id, TemplateType.Clean, "clean");
            SrtTranslateData.Clear(id, script, CancellationToken.None);
        }

        internal TranslatorBase CreateTranslator(ICommunicator communicator)
        {
            TranslatorBase translator = Task.TranslateType switch
            {
                TranslateDataType.KV => new KVTranslator(Task),
                TranslateDataType.Tpp => new TppTranslator(Task),
                TranslateDataType.Srt => new SrtTranslator(Task),
                _ => throw new NotSupportedException()
            };
            translator._communicator = communicator;
            translator.TranslateData.GetUntranslatedData();
            return translator;
        }

        internal Dictionary<string, string?> Successful(TranslatorBase translator) => translator switch
        {
            KVTranslator kv => kv.Data.Dic_Successful!.ToDictionary(p => p.Key, p => (string?)p.Value),
            TppTranslator tpp => tpp.Data.Dic_Successful!,
            _ => throw new NotSupportedException()
        };

        internal Dictionary<string, string?> Failed(TranslatorBase translator) => translator switch
        {
            KVTranslator kv => kv.Data.Dic_Failed!.ToDictionary(p => p.Key, p => (string?)p.Value),
            TppTranslator tpp => tpp.Data.Dic_Failed!,
            _ => throw new NotSupportedException()
        };

        internal string PathFor(GenerateFileType type) => PublicParams.GetFileName(Task.DicName, Task.FileName, Task.TranslateType, type);

        internal void WriteJson(GenerateFileType type, object value) => File.WriteAllText(PathFor(type), JsonConvert.SerializeObject(value));

        internal void WriteTemplate(TemplateType type, string name, string content)
        {
            string path = PublicParams.GetTemplateFilePath(id, type, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }

        private static ConfigSave_TranslatePrams Params() => new() { MaxTokens = 100, Stops = [] };

        public void Dispose()
        {
            ViewModelManager.ViewModel.Dispatcher = previousDispatcher!;
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class QueuedCommunicator(params string[] outputs) : ICommunicator
    {
        private readonly Queue<string> outputs = new(outputs);
        internal List<string> Inputs { get; } = [];

        public string Translate(PostDataBase postData, ExampleDialogue[] headers, ExampleDialogue[] histories, string inputText, out double speed)
        {
            speed = 0;
            Inputs.Add(inputText);
            return outputs.Dequeue();
        }

        public void Cancel() { }
        public void Dispose() { }
    }
}
