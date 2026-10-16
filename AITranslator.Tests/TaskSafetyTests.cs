using AITranslator.Translator.Models;
using AITranslator.Translator.Persistent;
using AITranslator.Translator.TranslateData;
using AITranslator.View.Models;
using System.Windows.Threading;

namespace AITranslator.Tests;

public class TaskSafetyTests
{
    [Theory]
    [InlineData("配置文件.json")]
    [InlineData("原始数据.json")]
    [InlineData("翻译成功.json")]
    [InlineData("普通译文.json")]
    public void MergedOutputCannotOverwriteInternalState(string fileName)
    {
        string merged = Path.GetFullPath(PublicParams.GetFileName("safe-task", fileName, TranslateDataType.KV, GenerateFileType.Merged));
        string root = Path.GetFullPath(PublicParams.GetDicName("safe-task"));
        Assert.Equal(Path.Combine(root, "output", fileName), merged);
        Assert.NotEqual(Path.GetFullPath(PublicParams.GetFileName("safe-task", TranslateDataType.KV, GenerateFileType.Config)), merged);
    }

    [Theory]
    [InlineData("../配置文件.json")]
    [InlineData("..\\配置文件.json")]
    [InlineData("C:/outside.json")]
    [InlineData(".. ")]
    public void OutputRejectsNonLeafFileNames(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => PublicParams.GetFileName("safe-task", name, TranslateDataType.KV, GenerateFileType.Merged));
    }

    [Theory]
    [InlineData("../outside")]
    [InlineData("..\\outside")]
    [InlineData("C:/outside")]
    public void TaskDirectoryRejectsTraversal(string name)
    {
        Assert.ThrowsAny<ArgumentException>(() => PublicParams.GetDicName(name));
        Assert.ThrowsAny<ArgumentException>(() => PublicParams.GetFileName(name, TranslateDataType.KV, GenerateFileType.Config));
    }

    [Theory]
    [InlineData(TranslateDataType.KV)]
    [InlineData(TranslateDataType.Tpp)]
    [InlineData(TranslateDataType.Txt)]
    [InlineData(TranslateDataType.Srt)]
    public void RetranslateFailedClearsPrimaryWithoutResurrectingBackup(TranslateDataType type)
    {
        string id = "retry-failed-" + Guid.NewGuid().ToString("N");
        string root = PublicParams.GetDicName(id);
        Directory.CreateDirectory(root);
        string path = PublicParams.GetFileName(id, type, GenerateFileType.Failed);
        void SaveFailure(string text)
        {
            if (type == TranslateDataType.Srt)
                SrtPersister.Save(new Dictionary<int, SrtData> { [10] = new(["00:00:01,000 --> 00:00:02,000", text]) }, path);
            else
                JsonPersister.Save(new Dictionary<string, string> { ["0"] = text }, path);
        }
        SaveFailure("old failure");
        SaveFailure("latest failure");
        var vm = ViewModelManager.ViewModel;
        vm.Dispatcher = Dispatcher.CurrentDispatcher;
        var previousActive = vm.ActiveTask;
        var previousType = vm.Communicator.CommunicatorType;
        vm.ActiveTask = new TranslationTask(); // Queue instead of starting a real request.
        vm.Communicator.CommunicatorType = CommunicatorType.OpenAI;
        var task = new TranslationTask { DicName = id, TranslateType = type, State = TaskState.Pause };
        try
        {
            task.ReTranslateFailed();
            int count = type == TranslateDataType.Srt ? SrtPersister.LoadInternal(path).Count : JsonPersister.LoadInternal<Dictionary<string, string>>(path).Count;
            Assert.Equal(0, count);
            Assert.Equal(TaskState.WaitTranslate, task.State);
        }
        finally
        {
            vm.ActiveTask = previousActive;
            vm.Communicator.CommunicatorType = previousType;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void BackupOnlyTaskConfigRecoversFileNameAndProgress()
    {
        string id = "config-recovery-" + Guid.NewGuid().ToString("N");
        string root = PublicParams.GetDicName(id);
        Directory.CreateDirectory(root);
        string path = PublicParams.GetFileName(id, TranslateDataType.KV, GenerateFileType.Config);
        File.WriteAllText(CheckpointFile.BackupPath(path), Newtonsoft.Json.JsonConvert.SerializeObject(new ConfigSave_Translate
        {
            FileName = "recovered.json", TranslateType = TranslateDataType.KV, Progress = 73, State = TaskState.Pause
        }));
        try
        {
            var task = new TranslationTask(new DirectoryInfo(root));
            Assert.Equal("recovered.json", task.FileName);
            Assert.Equal(73, task.Progress);
            Assert.False(File.Exists(path));
            Assert.True(File.Exists(CheckpointFile.BackupPath(path)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void BackupOnlyBaseConfigIsLoadedBeforeCreatingDefaults()
    {
        string previousDirectory = Environment.CurrentDirectory;
        string root = Path.Combine(Path.GetTempPath(), "base-config-recovery-" + Guid.NewGuid().ToString("N"));
        bool previousAgreement = ViewModelManager.ViewModel.AgreedStatement;
        Directory.CreateDirectory(root);
        Environment.CurrentDirectory = root;
        File.WriteAllText(CheckpointFile.BackupPath(PublicParams.ConfigPath_LoadModel), "{\"AgreedStatement\":true,\"Set\":{}}");
        try
        {
            ViewModelManager.LoadBaseConfig();
            Assert.True(ViewModelManager.ViewModel.AgreedStatement);
            Assert.False(File.Exists(PublicParams.ConfigPath_LoadModel));
        }
        finally
        {
            ViewModelManager.ViewModel.AgreedStatement = previousAgreement;
            Environment.CurrentDirectory = previousDirectory;
            Directory.Delete(root, true);
        }
    }

    [Fact]
    public void MissingTemplateDoesNotDeleteTaskOrLeaveItActive()
    {
        var vm = ViewModelManager.ViewModel;
        vm.Dispatcher = Dispatcher.CurrentDispatcher;
        string id = "task-safety-" + Guid.NewGuid().ToString("N");
        string templateName = "task-safety-" + Guid.NewGuid().ToString("N");
        string taskPath = PublicParams.GetDicName(id);
        string templatePath = PublicParams.GetTemplateFilePath(TemplateType.TemplateConfig, templateName);
        Directory.CreateDirectory(taskPath);
        Directory.CreateDirectory(Path.GetDirectoryName(templatePath)!);
        File.WriteAllText(Path.Combine(taskPath, "progress-marker.txt"), "valuable progress");
        JsonPersister.Save(new ConfigSave_DefaultTemplate { TemplateDic = "missing-" + Guid.NewGuid().ToString("N") }, templatePath);
        var task = new TranslationTask
        {
            DicName = id, FileName = "input.json", TranslateType = TranslateDataType.KV,
            TemplateConfig = new Template(templateName, TemplateType.TemplateConfig), State = TaskState.Pause
        };
        vm.UnfinishedTasks.Add(task);
        vm.ActiveTask = null!;
        try
        {
            task.Start(showErrorMsg: false);
            Assert.True(File.Exists(Path.Combine(taskPath, "progress-marker.txt")), "A missing template must not recycle the task directory.");
            Assert.Contains(task, vm.UnfinishedTasks);
            Assert.Null(vm.ActiveTask);
            Assert.Equal(TaskState.Pause, task.State);
            var mergeError = Assert.ThrowsAny<AITranslator.Exceptions.KnownException>(() => task.merge());
            Assert.IsNotType<AITranslator.Exceptions.DicNotFoundException>(mergeError);
            Assert.True(File.Exists(Path.Combine(taskPath, "progress-marker.txt")));
        }
        finally
        {
            vm.ActiveTask = null!;
            vm.UnfinishedTasks.Remove(task);
            if (Directory.Exists(taskPath)) Directory.Delete(taskPath, true);
            File.Delete(templatePath);
        }
    }
}
