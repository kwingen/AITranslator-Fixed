using AITranslator.Translator.Models;
using AITranslator.Translator.Persistent;
using AITranslator.Translator.TranslateData;
using AITranslator.Translator.Translation;
using AITranslator.View.Models;
using AITranslator.View.Windows;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace AITranslator.Tests;

public sealed class ReviewDataSafetyTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingTaskFolderCommandDoesNotAutomaticallyRemoveTask(bool merge)
    {
        RunOnSta(() =>
        {
            using var fixture = new MergeFixture(TranslateDataType.KV);
            Directory.Delete(PublicParams.GetDicName(fixture.Task.DicName), true);
            var messages = RunCommandWithClosedDialogs(() =>
            {
                if (merge) fixture.Task.MergeCommand.Execute(null);
                else fixture.Task.OpenDicCommand.Execute(null);
            });

            Assert.Single(messages);
            Assert.Contains(fixture.Task, ViewModelManager.ViewModel.UnfinishedTasks);
            Assert.Equal(TaskState.WaitMerge, fixture.Task.State);
        });
    }

    [Fact]
    public void MissingTemplateMergeCommandPreservesTaskAndCheckpointFiles()
    {
        RunOnSta(() =>
        {
            using var fixture = new MergeFixture(TranslateDataType.KV);
            fixture.Task.TemplateConfigParams.TemplateDic = "missing-template";
            JsonPersister.Save(fixture.Task.TemplateConfigParams, fixture.TemplateConfigPath);
            var before = File.ReadAllBytes(fixture.PathFor(GenerateFileType.Successful));

            var messages = RunCommandWithClosedDialogs(() => fixture.Task.MergeCommand.Execute(null));

            Assert.Single(messages);
            Assert.Contains("missing-template", messages[0]);
            Assert.DoesNotContain("未找到文件夹错误", messages[0]);
            Assert.Contains(fixture.Task, ViewModelManager.ViewModel.UnfinishedTasks);
            Assert.Equal(before, File.ReadAllBytes(fixture.PathFor(GenerateFileType.Successful)));
            Assert.Equal(TaskState.WaitMerge, fixture.Task.State);
        });
    }

    [Theory]
    [InlineData(TranslateDataType.KV)]
    [InlineData(TranslateDataType.Tpp)]
    [InlineData(TranslateDataType.Txt)]
    [InlineData(TranslateDataType.Srt)]
    public void RecoveredIncompleteCheckpointCannotOmitCleanedEntriesFromMerge(TranslateDataType type)
    {
        using var fixture = new MergeFixture(type);
        string successful = fixture.PathFor(GenerateFileType.Successful);
        string backup = CheckpointFile.BackupPath(successful);
        if (type == TranslateDataType.Srt)
            SrtPersister.Save(new Dictionary<int, SrtData> { [10] = new(["00:00:01,000 --> 00:00:02,000", "translated a"]) }, backup);
        else
            JsonPersister.Save(type switch
            {
                TranslateDataType.Txt => (object)new Dictionary<int, string> { [0] = "translated a" },
                TranslateDataType.Tpp => new Dictionary<string, Dictionary<string, string?>> { ["scene.csv"] = new() { ["a"] = "translated a" } },
                _ => new Dictionary<string, string> { ["a"] = "translated a" }
            }, backup);
        File.WriteAllText(successful, "corrupt primary");
        if (type == TranslateDataType.Srt)
            SrtPersister.Save(new Dictionary<int, SrtData>(), fixture.PathFor(GenerateFileType.Failed));
        else
            File.WriteAllText(fixture.PathFor(GenerateFileType.Failed), "{}");
        string output = type == TranslateDataType.Tpp
            ? Path.Combine(fixture.PathFor(GenerateFileType.Merged), "scene.csv") : fixture.PathFor(GenerateFileType.Merged);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "previous output");
        var translator = fixture.CreateTranslator();
        byte[] configBefore = File.ReadAllBytes(fixture.PathFor(GenerateFileType.Config));
        byte[] backupBefore = File.ReadAllBytes(backup);

        var error = Record.Exception(() => translator.MergeData());

        Assert.IsType<AITranslator.Exceptions.KnownException>(error);
        Assert.Contains(type switch { TranslateDataType.Txt => "1", TranslateDataType.Srt => "20", _ => "b" }, error.Message);
        Assert.Equal(TaskState.WaitMerge, fixture.Task.State);
        Assert.Equal("previous output", File.ReadAllText(output));
        Assert.Equal(configBefore, File.ReadAllBytes(fixture.PathFor(GenerateFileType.Config)));
        Assert.Equal("corrupt primary", File.ReadAllText(successful));
        Assert.Equal(backupBefore, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData("10\n00:00:01,000 --> 00:00:02,000\nfirst\n\n10\n00:00:03,000 --> 00:00:04,000\nduplicate\n\n")]
    [InlineData("999999999999999999999\n00:00:01,000 --> 00:00:02,000\noversized\n\n")]
    public void MalformedSrtPrimaryRecoversAndCanBeSafelyReplaced(string malformed)
    {
        using var fixture = new MergeFixture(TranslateDataType.Srt);
        string path = fixture.PathFor(GenerateFileType.Successful);
        string backup = CheckpointFile.BackupPath(path);
        File.Copy(path, backup);
        byte[] backupBefore = File.ReadAllBytes(backup);
        File.WriteAllText(path, malformed);

        Assert.Throws<AITranslator.Exceptions.FileLoadException>(() => SrtPersister.Load(path));
        Assert.Equal(new[] { 10, 20 }, SrtPersister.LoadInternal(path).Keys);
        Assert.Equal(malformed, File.ReadAllText(path));
        Assert.Equal(backupBefore, File.ReadAllBytes(backup));

        SrtPersister.Save(new Dictionary<int, SrtData> { [30] = new(["00:00:05,000 --> 00:00:06,000", "new translation"]) }, path);

        Assert.Equal(new[] { 30 }, SrtPersister.Load(path).Keys);
        Assert.Equal(backupBefore, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData("a,first\na,duplicate\n")]
    [InlineData("a,\"unterminated\n")]
    public void MalformedCsvPrimaryRecoversAndCanBeSafelyReplaced(string malformed)
    {
        using var fixture = new MergeFixture(TranslateDataType.Tpp);
        string path = Path.Combine(fixture.PathFor(GenerateFileType.Source), "scene.csv");
        string backup = CheckpointFile.BackupPath(path);
        File.Copy(path, backup);
        byte[] backupBefore = File.ReadAllBytes(backup);
        File.WriteAllText(path, malformed);

        Assert.Throws<AITranslator.Exceptions.FileLoadException>(() => CsvPersister.Load(path));
        Assert.Equal(new[] { "a", "b" }, CheckpointFile.Load(path, CsvPersister.Load).Keys);
        Assert.Equal(malformed, File.ReadAllText(path));
        Assert.Equal(backupBefore, File.ReadAllBytes(backup));

        CsvPersister.Save(new Dictionary<string, string?> { ["c"] = "new translation" }, path);

        Assert.Equal(new[] { "c" }, CsvPersister.Load(path).Keys);
        Assert.Equal(backupBefore, File.ReadAllBytes(backup));
    }

    [Theory]
    [InlineData(TranslateDataType.KV, false)]
    [InlineData(TranslateDataType.Tpp, false)]
    [InlineData(TranslateDataType.Txt, false)]
    [InlineData(TranslateDataType.Srt, false)]
    [InlineData(TranslateDataType.KV, true)]
    [InlineData(TranslateDataType.Tpp, true)]
    [InlineData(TranslateDataType.Txt, true)]
    [InlineData(TranslateDataType.Srt, true)]
    public void ManualMergeWriteFailureRestoresStateAndAllowsRetry(TranslateDataType type, bool failConfigSave)
    {
        RunOnSta(() =>
        {
            using var fixture = new MergeFixture(type);
            var translatorField = typeof(TranslationTask).GetField("_translator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            translatorField.SetValue(fixture.Task, fixture.CreateTranslator());
            string output = type == TranslateDataType.Tpp
                ? Path.Combine(fixture.PathFor(GenerateFileType.Merged), "scene.csv") : fixture.PathFor(GenerateFileType.Merged);
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            File.WriteAllText(output, "previous output");
            string blockedPath = failConfigSave ? fixture.PathFor(GenerateFileType.Config) : output;
            byte[] configBefore = File.ReadAllBytes(fixture.PathFor(GenerateFileType.Config));
            using (var blockedFile = new FileStream(blockedPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var messages = RunCommandWithClosedDialogs(() => fixture.Task.MergeCommand.Execute(null));

                Assert.Single(messages);
                Assert.Contains("保存文件错误", messages[0]);
                Assert.Equal(TaskState.WaitMerge, fixture.Task.State);
                Assert.Null(translatorField.GetValue(fixture.Task));
                Assert.Contains(fixture.Task, ViewModelManager.ViewModel.UnfinishedTasks);
                Assert.DoesNotContain(fixture.Task, ViewModelManager.ViewModel.CompletedTasks);
                Assert.Equal(configBefore, File.ReadAllBytes(fixture.PathFor(GenerateFileType.Config)));
                if (!failConfigSave) Assert.Equal("previous output", File.ReadAllText(output));
            }

            fixture.Task.merge();

            Assert.Equal(TaskState.Completed, fixture.Task.State);
            Assert.DoesNotContain(fixture.Task, ViewModelManager.ViewModel.UnfinishedTasks);
            Assert.Contains(fixture.Task, ViewModelManager.ViewModel.CompletedTasks);
            Assert.Null(translatorField.GetValue(fixture.Task));
        });
    }

    [Fact]
    public void ManualMergeUnexpectedOutputExceptionRestoresPreviousState()
    {
        using var fixture = new MergeFixture(TranslateDataType.KV);
        var translatorField = typeof(TranslationTask).GetField("_translator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        translatorField.SetValue(fixture.Task, fixture.CreateTranslator());
        fixture.Task.State = TaskState.Pause;
        string outputDirectory = Path.GetDirectoryName(fixture.PathFor(GenerateFileType.Merged))!;
        Directory.Delete(outputDirectory);
        File.WriteAllText(outputDirectory, "blocks directory creation");

        Assert.ThrowsAny<IOException>(() => fixture.Task.merge());

        Assert.Equal(TaskState.Pause, fixture.Task.State);
        Assert.Null(translatorField.GetValue(fixture.Task));
        Assert.Contains(fixture.Task, ViewModelManager.ViewModel.UnfinishedTasks);
    }

    [Fact]
    public void ManualMergeCheckpointReadFailureDiscardsExistingTranslator()
    {
        RunOnSta(() =>
        {
            using var fixture = new MergeFixture(TranslateDataType.KV);
            var translatorField = typeof(TranslationTask).GetField("_translator", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
            translatorField.SetValue(fixture.Task, fixture.CreateTranslator());
            File.WriteAllText(fixture.PathFor(GenerateFileType.Failed), "not json");

            var messages = RunCommandWithClosedDialogs(() => fixture.Task.MergeCommand.Execute(null));

            Assert.Single(messages);
            Assert.Contains("加载文件错误", messages[0]);
            Assert.Equal(TaskState.WaitMerge, fixture.Task.State);
            Assert.Null(translatorField.GetValue(fixture.Task));
            Assert.Contains(fixture.Task, ViewModelManager.ViewModel.UnfinishedTasks);
        });
    }

    [Theory]
    [InlineData(TranslateDataType.KV)]
    [InlineData(TranslateDataType.Tpp)]
    [InlineData(TranslateDataType.Txt)]
    [InlineData(TranslateDataType.Srt)]
    public void FreshManualMergeFailurePreservesPersistedWaitMergeState(TranslateDataType type)
    {
        using var fixture = new MergeFixture(type);
        string output = type == TranslateDataType.Tpp
            ? Path.Combine(fixture.PathFor(GenerateFileType.Merged), "scene.csv") : fixture.PathFor(GenerateFileType.Merged);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, "previous output");
        byte[] configBefore = File.ReadAllBytes(fixture.PathFor(GenerateFileType.Config));
        using (var locked = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.Throws<AITranslator.Exceptions.FileSaveException>(() => fixture.Task.merge());
            Assert.Equal(TaskState.WaitMerge, fixture.Task.State);
            Assert.Equal(configBefore, File.ReadAllBytes(fixture.PathFor(GenerateFileType.Config)));
            var reloaded = JsonPersister.LoadInternal<ConfigSave_Translate>(fixture.PathFor(GenerateFileType.Config));
            Assert.Equal(TaskState.WaitMerge, reloaded.State);
        }
    }

    private static readonly Lazy<Dispatcher> UiDispatcher = new(() =>
    {
        Dispatcher? dispatcher = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            application.Resources["English"] = new System.Windows.Media.FontFamily("Arial");
            dispatcher = Dispatcher.CurrentDispatcher;
            ready.Set();
            Dispatcher.Run();
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "WPF dispatcher did not start.");
        return dispatcher!;
    });

    private static void RunOnSta(Action action)
    {
        Exception? error = null;
        UiDispatcher.Value.Invoke(() =>
        {
            try { action(); }
            catch (Exception exception) { error = exception; }
        });
        if (error is not null) ExceptionDispatchInfo.Capture(error).Throw();
    }

    private static List<string> RunCommandWithClosedDialogs(Action command)
    {
        var previousOwner = Window_Message.DefaultOwner;
        var owner = new Window();
        Window_Message.DefaultOwner = owner;
        var messages = new List<string>();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        timer.Tick += (_, _) =>
        {
            foreach (var dialog in PresentationSource.CurrentSources.OfType<HwndSource>()
                         .Select(source => source.RootVisual).OfType<Window_Message>().ToArray())
            {
                messages.Add(dialog.Message ?? "");
                dialog.DialogResult = true;
            }
        };
        timer.Start();
        try { command(); }
        finally
        {
            timer.Stop();
            owner.Close();
            Window_Message.DefaultOwner = previousOwner;
        }
        return messages;
    }

    private sealed class MergeFixture : IDisposable
    {
        private readonly string previousDirectory = Environment.CurrentDirectory;
        private readonly Dispatcher? previousDispatcher;
        private readonly string root;
        internal TranslationTask Task { get; }
        internal string TemplateConfigPath => PublicParams.GetTemplateFilePath(TemplateType.TemplateConfig, Task.DicName);

        internal MergeFixture(TranslateDataType type)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            string id = "review-safety-" + Guid.NewGuid().ToString("N");
            root = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(), id);
            Directory.CreateDirectory(root);
            Environment.CurrentDirectory = root;
            previousDispatcher = ViewModelManager.ViewModel.Dispatcher;
            ViewModelManager.ViewModel.Dispatcher = Dispatcher.CurrentDispatcher;
            Task = new TranslationTask
            {
                DicName = id, FileName = type switch
                {
                    TranslateDataType.Tpp => "result", TranslateDataType.Srt => "result.srt",
                    TranslateDataType.Txt => "result.txt", _ => "result.json"
                },
                TranslateType = type, State = TaskState.WaitMerge,
                TemplateConfig = new Template(id, TemplateType.TemplateConfig),
                TemplateConfigParams = new ConfigSave_DefaultTemplate
                {
                    TemplateDic = id, PromptTemplate = "prompt", ReplaceTemplate = "replace", VerificationTemplate = "verify"
                }
            };
            foreach (var template in new[]
                     {
                         (TemplateType.Prompt, "prompt", "[{\"role\":\"user\",\"content\":\"\"}]"),
                         (TemplateType.Replace, "replace", "{}"),
                         (TemplateType.Verification, "verify", "return (true, \"\");")
                     })
            {
                string path = PublicParams.GetTemplateFilePath(id, template.Item1, template.Item2);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, template.Item3);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(TemplateConfigPath)!);
            JsonPersister.Save(Task.TemplateConfigParams, TemplateConfigPath);
            Directory.CreateDirectory(PublicParams.GetDicName(id));
            Directory.CreateDirectory(Path.GetDirectoryName(PathFor(GenerateFileType.Merged))!);
            if (type == TranslateDataType.Txt)
            {
                TxtPersister.Save(["a", "b"], PathFor(GenerateFileType.Source));
                TxtPersister.Save(["a", "b"], PathFor(GenerateFileType.Cleaned));
                JsonPersister.Save(new Dictionary<int, string> { [0] = "translated a", [1] = "translated b" }, PathFor(GenerateFileType.Successful));
            }
            else if (type == TranslateDataType.Tpp)
            {
                var source = new Dictionary<string, Dictionary<string, string?>> { ["scene.csv"] = new() { ["a"] = "a", ["b"] = "b" } };
                CsvPersister.SaveToFolder(PathFor(GenerateFileType.Source), source);
                JsonPersister.Save(source, PathFor(GenerateFileType.Cleaned));
                JsonPersister.Save(source, PathFor(GenerateFileType.Successful));
            }
            else if (type == TranslateDataType.Srt)
            {
                var cues = new Dictionary<int, SrtData>
                {
                    [10] = new(["00:00:01,000 --> 00:00:02,000", "a"]),
                    [20] = new(["00:00:03,000 --> 00:00:04,000", "b"])
                };
                SrtPersister.Save(cues, PathFor(GenerateFileType.Source));
                SrtPersister.Save(cues, PathFor(GenerateFileType.Cleaned));
                SrtPersister.Save(cues, PathFor(GenerateFileType.Successful));
            }
            else
            {
                var source = new Dictionary<string, string> { ["a"] = "a", ["b"] = "b" };
                JsonPersister.Save(source, PathFor(GenerateFileType.Source));
                JsonPersister.Save(source, PathFor(GenerateFileType.Cleaned));
                JsonPersister.Save(source, PathFor(GenerateFileType.Successful));
            }
            Task.SaveConfig();
            ViewModelManager.ViewModel.UnfinishedTasks.Add(Task);
        }

        internal string PathFor(GenerateFileType type) => PublicParams.GetFileName(Task.DicName, Task.FileName, Task.TranslateType, type);
        internal TranslatorBase CreateTranslator() => Task.TranslateType switch
        {
            TranslateDataType.KV => new KVTranslator(Task), TranslateDataType.Tpp => new TppTranslator(Task),
            TranslateDataType.Srt => new SrtTranslator(Task), TranslateDataType.Txt => new TxtTranslator(Task),
            _ => throw new NotSupportedException()
        };

        public void Dispose()
        {
            ViewModelManager.ViewModel.UnfinishedTasks.Remove(Task);
            ViewModelManager.ViewModel.CompletedTasks.Remove(Task);
            ViewModelManager.ViewModel.Dispatcher = previousDispatcher!;
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, true);
        }
    }
}
