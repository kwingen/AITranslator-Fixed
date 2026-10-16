using AITranslator.Translator.Models;
using AITranslator.Translator.Persistent;
using AITranslator.Translator.Translation;
using AITranslator.Translator.TranslateData;
using AITranslator.View.Models;
using System.Windows.Threading;

namespace AITranslator.Tests;

public class TxtMergeTests
{
    [Fact]
    public void MergeCreatesSeparatedOutputDirectoryAndWritesAllLines()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
        string id = "txt-merge-" + Guid.NewGuid().ToString("N");
        var task = new TranslationTask
        {
            DicName = id, FileName = "result.txt", TranslateType = TranslateDataType.Txt,
            State = TaskState.Pause,
            TemplateConfigParams = new ConfigSave_DefaultTemplate
            { TemplateDic = id, PromptTemplate = "prompt", VerificationTemplate = "verify", ReplaceTemplate = "replace" }
        };
        ViewModelManager.ViewModel.Dispatcher = Dispatcher.CurrentDispatcher;
        Directory.CreateDirectory(PublicParams.GetDicName(id));
        foreach (var template in new[] { (TemplateType.Prompt, "prompt", "[{\"role\":\"user\",\"content\":\"\"}]"), (TemplateType.Verification, "verify", "return (true, \"\");"), (TemplateType.Replace, "replace", "{}") })
        {
            string path = PublicParams.GetTemplateFilePath(id, template.Item1, template.Item2);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, template.Item3);
        }
        TxtPersister.Save(["first", "second"], PublicParams.GetFileName(id, TranslateDataType.Txt, GenerateFileType.Source));
        TxtPersister.Save(["first", "second"], PublicParams.GetFileName(id, TranslateDataType.Txt, GenerateFileType.Cleaned));
        JsonPersister.Save(new Dictionary<int, string> { [0] = "第一行", [1] = "第二行" }, PublicParams.GetFileName(id, TranslateDataType.Txt, GenerateFileType.Successful));
        try
        {
            var translator = new TxtTranslator(task);
            translator.MergeData();
            Assert.Equal(new[] { "第一行", "第二行" }, TxtPersister.Load(PublicParams.GetFileName(id, task.FileName, TranslateDataType.Txt, GenerateFileType.Merged)));
            Assert.Equal(TaskState.Completed, task.State);
        }
        finally
        {
            Directory.Delete(PublicParams.GetDicName(id), true);
            Directory.Delete(Path.Combine(PublicParams.TemplatesDic, id), true);
        }
    }
}
