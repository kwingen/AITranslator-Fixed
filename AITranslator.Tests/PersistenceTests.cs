using System.IO;
using System.Text;
using AITranslator.Translator.Persistent;
using Xunit;

namespace AITranslator.Tests;

public sealed class PersistenceTests : IDisposable
{
    // AssemblyInfo disables all test parallelization; restore process cwd in Dispose.
    private readonly string previousDirectory = Directory.GetCurrentDirectory();
    private readonly string root = Path.Combine(Path.GetTempPath(), "AITranslator-Persistence-" + Guid.NewGuid().ToString("N"));

    public PersistenceTests()
    {
        Directory.CreateDirectory(root);
        Directory.SetCurrentDirectory(root);
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Theory]
    [InlineData("json", "{\"key\":\"current\"}", "{\"key\":\"stale\"}")]
    [InlineData("txt", "current", "stale")]
    [InlineData("csv", "key,current\n", "key,stale\n")]
    [InlineData("srt", "1\n00:00:00,000 --> 00:00:01,000\ncurrent\n\n", "1\n00:00:00,000 --> 00:00:01,000\nstale\n\n")]
    public void ExternalLoad_ReadsPrimaryWithoutChangingAdjacentBackup(string format, string current, string stale)
    {
        string path = Path.Combine(root, "input." + format);
        string backup = Path.Combine(root, "input_bak." + format);
        File.WriteAllText(path, current);
        File.WriteAllText(backup, stale);
        byte[] before = File.ReadAllBytes(path);
        byte[] beforeBackup = File.ReadAllBytes(backup);

        string loaded = format switch
        {
            "json" => JsonPersister.Load<Dictionary<string, string>>(path)["key"],
            "txt" => TxtPersister.Load(path).Single(),
            "csv" => CsvPersister.Load(path)["key"]!,
            "srt" => SrtPersister.Load(path)[1].Text,
            _ => throw new InvalidOperationException()
        };

        Assert.Equal("current", loaded);
        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.True(File.Exists(backup));
        Assert.Equal(beforeBackup, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData("KV", false)]
    [InlineData("Txt", false)]
    [InlineData("Srt", false)]
    [InlineData("Tpp", false)]
    [InlineData("KV", true)]
    [InlineData("Txt", true)]
    [InlineData("Srt", true)]
    [InlineData("Tpp", true)]
    public void TranslateData_RecoversBackupOnlyCheckpointsBeforeInitializingEmptyProgress(string kind, bool legacy)
    {
        var type = Enum.Parse<AITranslator.Translator.TranslateData.TranslateDataType>(kind);
        string dicName = "isolated-task";
        string State(AITranslator.Translator.Models.GenerateFileType fileType) =>
            AITranslator.Translator.Models.PublicParams.GetFileName(dicName, type, fileType);
        string Backup(string path) => legacy
            ? Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_bak" + Path.GetExtension(path))
            : CheckpointFile.BackupPath(path);
        string source = State(AITranslator.Translator.Models.GenerateFileType.Source);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        string cleaned;
        string successful;
        string failed;
        if (kind == "Tpp")
        {
            Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "part.csv"), "a,source-a\nb,source-b\n");
            cleaned = "{\"part.csv\":{\"a\":\"source-a\",\"b\":\"source-b\"}}";
            successful = "{\"part.csv\":{\"a\":\"translated-a\"}}";
            failed = "{\"part.csv\":{\"b\":\"manual-b\"}}";
        }
        else if (kind == "Srt")
        {
            successful = "1\n00:00:00,000 --> 00:00:01,000\ntranslated-a\n\n";
            failed = "2\n00:00:01,000 --> 00:00:02,000\nmanual-b\n\n";
            cleaned = successful + failed;
            File.WriteAllText(source, cleaned);
        }
        else
        {
            cleaned = kind == "Txt" ? "source-a\nsource-b" : "{\"a\":\"source-a\",\"b\":\"source-b\"}";
            successful = kind == "Txt" ? "{\"0\":\"translated-a\"}" : "{\"a\":\"translated-a\"}";
            failed = kind == "Txt" ? "{\"1\":\"manual-b\"}" : "{\"b\":\"manual-b\"}";
            File.WriteAllText(source, cleaned);
        }
        File.WriteAllText(Backup(State(AITranslator.Translator.Models.GenerateFileType.Cleaned)), cleaned);
        File.WriteAllText(Backup(State(AITranslator.Translator.Models.GenerateFileType.Successful)), successful);
        File.WriteAllText(Backup(State(AITranslator.Translator.Models.GenerateFileType.Failed)), failed);

        AITranslator.Translator.TranslateData.ITranslateData data = kind switch
        {
            "KV" => new AITranslator.Translator.TranslateData.KVTranslateData(dicName, "input.json"),
            "Txt" => new AITranslator.Translator.TranslateData.TxtTranslateData(dicName, "input.txt"),
            "Srt" => new AITranslator.Translator.TranslateData.SrtTranslateData(dicName, "input.srt"),
            "Tpp" => new AITranslator.Translator.TranslateData.TppTranslateData(dicName, "input"),
            _ => throw new InvalidOperationException()
        };
        Assert.True(data.IsCleaned);
        Assert.Equal(100d, data.GetProgress());
        data.GetUntranslatedData();
        bool hasFailed = kind switch
        {
            "KV" => AITranslator.Translator.TranslateData.KVTranslateData.HasFailedData(dicName),
            "Txt" => AITranslator.Translator.TranslateData.TxtTranslateData.HasFailedData(dicName),
            "Srt" => AITranslator.Translator.TranslateData.SrtTranslateData.HasFailedData(dicName),
            "Tpp" => AITranslator.Translator.TranslateData.TppTranslateData.HasFailedData(dicName),
            _ => false
        };
        Assert.True(hasFailed);
    }

    [Theory]
    [InlineData("json")]
    [InlineData("txt")]
    [InlineData("srt")]
    [InlineData("csv")]
    public void Save_KeepsPreviousCommittedVersionAsRecoverableBackup(string format)
    {
        string path = Path.Combine(root, "state." + format);
        SaveValue(format, path, "old-progress");
        byte[] previous = File.ReadAllBytes(path);

        SaveValue(format, path, "new-progress");

        Assert.Equal("new-progress", LoadValue(format, path));
        string backup = CheckpointFile.BackupPath(path);
        Assert.True(File.Exists(backup), "A committed save must retain the previous version.");
        Assert.Equal(previous, File.ReadAllBytes(backup));
        Assert.Equal("old-progress", LoadValue(format, backup));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("")]
    public void InternalJsonLoad_RecoversNullOrEmptyPrimaryWithoutChangingCandidates(string invalid)
    {
        string path = Path.Combine(root, "config.json");
        string backup = CheckpointFile.BackupPath(path);
        File.WriteAllText(path, invalid);
        File.WriteAllText(backup, "{\"key\":\"recoverable-progress\"}");

        var loaded = JsonPersister.LoadInternal<Dictionary<string, string>>(path);

        Assert.NotNull(loaded);
        Assert.Equal("recoverable-progress", loaded["key"]);
        Assert.Equal(invalid, File.ReadAllText(path));
        Assert.Equal("{\"key\":\"recoverable-progress\"}", File.ReadAllText(backup));
    }

    [Fact]
    public void InternalSrtLoad_RecoversStructurallyIncompletePrimary()
    {
        string path = Path.Combine(root, "checkpoint.srt");
        string backup = CheckpointFile.BackupPath(path);
        File.WriteAllText(path, "1");
        SaveValue("srt", backup, "recoverable-progress");

        var loaded = SrtPersister.LoadInternal(path);

        Assert.Equal("recoverable-progress", loaded[1].Text);
        Assert.Equal("1", File.ReadAllText(path));
        Assert.True(File.Exists(backup));
    }

    [Theory]
    [InlineData("json", "{")]
    [InlineData("json", "null")]
    [InlineData("srt", "1")]
    public void Save_DoesNotReplaceRecoverableBackupWithCorruptPrimary(string format, string corrupt)
    {
        string path = Path.Combine(root, "state." + format);
        SaveValue(format, path, "recoverable-progress");
        SaveValue(format, path, "current-progress");
        string backup = CheckpointFile.BackupPath(path);
        byte[] previous = File.ReadAllBytes(backup);
        File.WriteAllText(path, corrupt);

        SaveValue(format, path, "new-progress");

        Assert.Equal("new-progress", LoadValue(format, path));
        Assert.Equal(previous, File.ReadAllBytes(backup));
        Assert.Equal("recoverable-progress", LoadValue(format, backup));
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("txt", false)]
    [InlineData("csv", false)]
    [InlineData("srt", false)]
    [InlineData("json", true)]
    [InlineData("txt", true)]
    [InlineData("csv", true)]
    [InlineData("srt", true)]
    public void FailedSave_WithRealFileLockPreservesPrimaryAndBackup(string format, bool lockBackup)
    {
        string path = Path.Combine(root, "state." + format);
        SaveValue(format, path, "previous-progress");
        SaveValue(format, path, "current-progress");
        string backup = CheckpointFile.BackupPath(path);
        byte[] primaryBefore = File.ReadAllBytes(path);
        byte[] backupBefore = File.ReadAllBytes(backup);

        using (var held = new FileStream(lockBackup ? backup : path, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<AITranslator.Exceptions.FileSaveException>(() => SaveValue(format, path, "uncommitted"));
            Assert.Equal(primaryBefore, File.ReadAllBytes(path));
            Assert.Equal(backupBefore, File.ReadAllBytes(backup));
        }
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData("json", false)]
    [InlineData("txt", false)]
    [InlineData("csv", false)]
    [InlineData("srt", false)]
    [InlineData("json", true)]
    [InlineData("txt", true)]
    [InlineData("csv", true)]
    [InlineData("srt", true)]
    public void FailedSave_WithBackupOnlyAndCommitPathConflictPreservesRecovery(string format, bool legacy)
    {
        string path = Path.Combine(root, "state." + format);
        string backup = legacy
            ? Path.Combine(root, "state_bak." + format)
            : CheckpointFile.BackupPath(path);
        SaveValue(format, backup, "only-recoverable-progress");
        byte[] before = File.ReadAllBytes(backup);
        Directory.CreateDirectory(path); // Actual OS failure at atomic commit, not a fake filesystem.

        Assert.Throws<AITranslator.Exceptions.FileSaveException>(() => SaveValue(format, path, "uncommitted"));

        Assert.Equal(before, File.ReadAllBytes(backup));
        Assert.Equal("only-recoverable-progress", LoadValue(format, backup));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Theory]
    [InlineData("json")]
    [InlineData("txt")]
    [InlineData("srt")]
    public void InternalLoad_PreservesManuallyEditedPrimaryDespiteStaleBackup(string format)
    {
        string path = Path.Combine(root, "failed." + format);
        SaveValue(format, path, "stale");
        SaveValue(format, path, "manual-correction");
        byte[] primary = File.ReadAllBytes(path);
        string loaded = format switch
        {
            "json" => JsonPersister.LoadInternal<Dictionary<string, string>>(path)["key"],
            "txt" => TxtPersister.LoadInternal(path).Single(),
            "srt" => SrtPersister.LoadInternal(path)[1].Text,
            _ => throw new InvalidOperationException()
        };
        Assert.Equal("manual-correction", loaded);
        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal("stale", LoadValue(format, CheckpointFile.BackupPath(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InternalJsonLoad_TriesLegacyRecoveryWhenNewBackupIsCorrupt(bool primaryExists)
    {
        string path = Path.Combine(root, "state.json");
        if (primaryExists) File.WriteAllText(path, "{");
        string backup = CheckpointFile.BackupPath(path);
        string legacy = Path.Combine(root, "state_bak.json");
        File.WriteAllText(backup, "{");
        File.WriteAllText(legacy, "{\"key\":\"legacy-progress\"}");

        var loaded = JsonPersister.LoadInternal<Dictionary<string, string>>(path);

        Assert.Equal("legacy-progress", loaded["key"]);
        Assert.Equal("{", File.ReadAllText(backup));
        Assert.Equal("{\"key\":\"legacy-progress\"}", File.ReadAllText(legacy));
    }

    [Fact]
    public void JsonSave_HideFlagAlsoAppliesWhenReplacingExistingPrimary()
    {
        string path = Path.Combine(root, "config.json");
        JsonPersister.Save(new { value = "old" }, path);

        JsonPersister.Save(new { value = "new" }, path, hide: true);

        Assert.True((File.GetAttributes(path) & FileAttributes.Hidden) != 0);
        Assert.Equal("new", JsonPersister.Load<Dictionary<string, string>>(path)["value"]);
    }

    [Fact]
    public void JsonSave_SerializationFailureDoesNotTouchCommittedProgress()
    {
        string path = Path.Combine(root, "state.json");
        SaveValue("json", path, "previous-progress");
        SaveValue("json", path, "current-progress");
        byte[] primary = File.ReadAllBytes(path);
        byte[] backup = File.ReadAllBytes(CheckpointFile.BackupPath(path));
        var cyclic = new Dictionary<string, object>();
        cyclic["self"] = cyclic;

        Assert.Throws<Newtonsoft.Json.JsonSerializationException>(() => JsonPersister.Save(cyclic, path));

        Assert.Equal(primary, File.ReadAllBytes(path));
        Assert.Equal(backup, File.ReadAllBytes(CheckpointFile.BackupPath(path)));
        Assert.Empty(Directory.GetFiles(root, "*.tmp"));
    }

    [Fact]
    public void CsvFolderLoad_DoesNotImportNewManagedBackupsAsAdditionalSourceFiles()
    {
        string path = Path.Combine(root, "source.csv");
        SaveValue("csv", path, "previous");
        SaveValue("csv", path, "current");

        var loaded = CsvPersister.LoadFromFolder(root);

        Assert.Single(loaded);
        Assert.Equal("current", loaded["source.csv"]["key"]);
        Assert.True(File.Exists(CheckpointFile.BackupPath(path)));
    }

    private static void SaveValue(string format, string path, string value)
    {
        switch (format)
        {
            case "json": JsonPersister.Save(new Dictionary<string, string> { ["key"] = value }, path); break;
            case "txt": TxtPersister.Save(new List<string> { value }, path); break;
            case "csv": CsvPersister.Save(new Dictionary<string, string?> { ["key"] = value }, path); break;
            case "srt": SrtPersister.Save(new Dictionary<int, AITranslator.Translator.TranslateData.SrtData>
            {
                [1] = new(new[] { "00:00:00,000 --> 00:00:01,000", value })
            }, path); break;
            default: throw new InvalidOperationException();
        }
    }

    private static string LoadValue(string format, string path) => format switch
    {
        "json" => JsonPersister.Load<Dictionary<string, string>>(path)["key"],
        "txt" => TxtPersister.Load(path).Single(),
        "csv" => CsvPersister.Load(path)["key"]!,
        "srt" => SrtPersister.Load(path)[1].Text,
        _ => throw new InvalidOperationException()
    };

    public void Dispose()
    {
        Directory.SetCurrentDirectory(previousDirectory);
        if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
    }
}
