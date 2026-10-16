using AITranslator.Translator.Communicator;
using AITranslator.Translator.Models;
using AITranslator.Translator.Persistent;
using AITranslator.Translator.PostData;
using AITranslator.Translator.Translation;
using AITranslator.Translator.TranslateData;
using AITranslator.View.Models;
using Newtonsoft.Json;
using System.Windows.Threading;

namespace AITranslator.Tests;

// Runs the real translators, verification scripts and checkpoint readers/writers in isolation.
internal sealed class AdaptiveCheckpointFixture : IDisposable
{
    private readonly string previousDirectory = Environment.CurrentDirectory;
    private readonly Dispatcher? previousDispatcher;
    private readonly string root;
    internal TranslationTask Task { get; }

    internal AdaptiveCheckpointFixture(TranslateDataType type, params string[] entries)
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        string id = "adaptive-checkpoint-" + Guid.NewGuid().ToString("N");
        root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), id);
        Directory.CreateDirectory(root);
        Environment.CurrentDirectory = root;
        previousDispatcher = ViewModelManager.ViewModel.Dispatcher;
        ViewModelManager.ViewModel.Dispatcher = Dispatcher.CurrentDispatcher;
        Task = new TranslationTask
        {
            DicName = id,
            FileName = type switch { TranslateDataType.Tpp => "translated", TranslateDataType.Txt => "translated.txt", _ => "translated.json" },
            TranslateType = type,
            TemplateConfigParams = new ConfigSave_DefaultTemplate
            {
                TemplateDic = id, PromptTemplate = "prompt", ReplaceTemplate = "replace", VerificationTemplate = "verify",
                HistoryCount = 2,
                TranslatePrams_FirstSingle = new() { MaxTokens = 101, Stops = [] },
                TranslatePrams_FirstMult = new() { MaxTokens = 102, Stops = [] },
                TranslatePrams_Retry = new() { MaxTokens = 103, Stops = [] }
            }
        };
        WriteTemplate(TemplateType.Prompt, "prompt", "[{\"role\":\"user\",\"content\":\"\"}]");
        WriteTemplate(TemplateType.Replace, "replace", "{}");
        // Exercise verification, including retry paths, without requiring a remote provider.
        WriteVerification("return (Translated != \"invalid\", \"invalid output\");");
        Directory.CreateDirectory(PublicParams.GetDicName(id));
        var values = entries.Select((entry, i) => (entry, i)).ToDictionary(item => "key-" + item.i, item => item.entry);
        if (type == TranslateDataType.KV)
        {
            JsonPersister.Save(values, PathFor(GenerateFileType.Source));
            JsonPersister.Save(values, PathFor(GenerateFileType.Cleaned));
        }
        else if (type == TranslateDataType.Tpp)
        {
            var source = new Dictionary<string, Dictionary<string, string?>>
            {
                ["scene.csv"] = values.ToDictionary(item => item.Key, item => (string?)item.Value)
            };
            CsvPersister.SaveToFolder(PathFor(GenerateFileType.Source), source);
            JsonPersister.Save(source, PathFor(GenerateFileType.Cleaned));
        }
        else if (type == TranslateDataType.Txt)
        {
            TxtPersister.Save(entries.ToList(), PathFor(GenerateFileType.Source));
            TxtPersister.Save(entries.ToList(), PathFor(GenerateFileType.Cleaned));
        }
        else
            throw new NotSupportedException();
    }

    internal TranslatorBase Create(ICommunicator communicator)
    {
        TranslatorBase translator = Task.TranslateType switch
        {
            TranslateDataType.KV => new KVTranslator(Task),
            TranslateDataType.Tpp => new TppTranslator(Task),
            TranslateDataType.Txt => new TxtTranslator(Task),
            _ => throw new NotSupportedException()
        };
        translator._communicator = communicator;
        translator.TranslateData.GetUntranslatedData();
        return translator;
    }

    internal string Key(int index) => Task.TranslateType switch
    {
        TranslateDataType.Tpp => "scene.csv||key-" + index,
        TranslateDataType.Txt => index.ToString(),
        _ => "key-" + index
    };

    internal Dictionary<string, string> Successful(TranslatorBase translator) => translator switch
    {
        KVTranslator kv => kv.Data.Dic_Successful!,
        TppTranslator tpp => tpp.Data.Dic_Successful!.ToDictionary(item => item.Key, item => item.Value!),
        TxtTranslator txt => txt.Data.Dic_Successful!.ToDictionary(item => item.Key.ToString(), item => item.Value),
        _ => throw new NotSupportedException()
    };

    internal Dictionary<string, string> Failed(TranslatorBase translator) => translator switch
    {
        KVTranslator kv => kv.Data.Dic_Failed!,
        TppTranslator tpp => tpp.Data.Dic_Failed!.ToDictionary(item => item.Key, item => item.Value!),
        TxtTranslator txt => txt.Data.Dic_Failed!.ToDictionary(item => item.Key.ToString(), item => item.Value),
        _ => throw new NotSupportedException()
    };

    internal Dictionary<string, string> ReadSuccessfulCheckpoint()
    {
        string path = PathFor(GenerateFileType.Successful);
        if (!File.Exists(path)) return [];
        if (Task.TranslateType == TranslateDataType.Tpp)
        {
            var nested = JsonConvert.DeserializeObject<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(path))!;
            return nested.SelectMany(file => file.Value.Select(item => (Key: file.Key + "||" + item.Key, item.Value)))
                .ToDictionary(item => item.Key, item => item.Value);
        }
        return JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path))!;
    }

    internal string PathFor(GenerateFileType type) => PublicParams.GetFileName(Task.DicName, Task.FileName, Task.TranslateType, type);

    internal void WriteVerification(string script) => WriteTemplate(TemplateType.Verification, "verify", script);

    private void WriteTemplate(TemplateType type, string name, string text)
    {
        string path = PublicParams.GetTemplateFilePath(Task.DicName, type, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        ViewModelManager.ViewModel.Dispatcher = previousDispatcher!;
        Environment.CurrentDirectory = previousDirectory;
        Directory.Delete(root, recursive: true);
    }
}

internal sealed class AdaptiveCheckpointCommunicator(Func<string, ExampleDialogue[], string> reply) : ICommunicator
{
    internal List<string> Inputs { get; } = [];
    internal List<int> HistoryCounts { get; } = [];
    internal List<int> TokenLimits { get; } = [];
    internal string[] Sources => Inputs.Select(input => input.TrimStart('\n')).ToArray();

    public string Translate(PostDataBase parameters, ExampleDialogue[] headers, ExampleDialogue[] histories, string input, out double speed)
    {
        speed = 1;
        Inputs.Add(input);
        HistoryCounts.Add(histories.Length);
        TokenLimits.Add(parameters.max_tokens);
        return reply(input.TrimStart('\n'), histories);
    }

    public void Cancel() { }
    public void Dispose() { }
}
