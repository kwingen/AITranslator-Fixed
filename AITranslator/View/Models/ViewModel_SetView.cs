using AITranslator.Mail;
using AITranslator.Translator.Models;
using AITranslator.Translator.Persistent;
using AITranslator.Translator.TranslateData;
using AITranslator.View.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;

namespace AITranslator.View.Models
{
    /// <summary>
    /// 用于界面绑定的ViewModel
    /// </summary>
    public partial class ViewModel_SetView : ObservableValidator
    {
        /// <summary>
        /// 默认模板_MTool
        /// </summary>
        [ObservableProperty]
        private Template? defaultTemplate_MTool;
        /// <summary>
        /// 默认模板_T++
        /// </summary>
        [ObservableProperty]
        private Template? defaultTemplate_Tpp;
        /// <summary>
        /// 默认模板_Srt
        /// </summary>
        [ObservableProperty]
        private Template? defaultTemplate_Srt;
        /// <summary>
        /// 默认模板_Txt
        /// </summary>
        [ObservableProperty]
        private Template? defaultTemplate_Txt;
        /// <summary>
        /// 启用邮件通知
        /// </summary>
        [ObservableProperty]
        private bool enableEmail;
        /// <summary>
        /// 邮箱地址
        /// </summary>
        public string EmailAddress
        {
            get => CaptureCredentialSnapshot().EmailAddress;
            set => SetCredentialProperty(value ?? string.Empty, nameof(EmailAddress), current => current.EmailAddress,
                (current, next) => current with { EmailAddress = next, EmailPassword = string.Empty }, true);
        }
        /// <summary>
        /// 邮箱密码
        /// </summary>
        public string EmailPassword
        {
            get => CaptureCredentialSnapshot().EmailPassword;
            set => SetCredentialProperty(value ?? string.Empty, nameof(EmailPassword), current => current.EmailPassword,
                (current, next) => current with { EmailPassword = next });
        }
        /// <summary>
        /// SMTP服务器地址
        /// </summary>
        public string SmtpAddress
        {
            get => CaptureCredentialSnapshot().SmtpAddress;
            set => SetCredentialProperty(value ?? string.Empty, nameof(SmtpAddress), current => current.SmtpAddress,
                (current, next) => current with { SmtpAddress = next, EmailPassword = string.Empty }, true);
        }
        /// <summary>
        /// SMTP服务器端口
        /// </summary>
        public ushort SmtpPort
        {
            get => CaptureCredentialSnapshot().SmtpPort;
            set => SetCredentialProperty(value, nameof(SmtpPort), current => current.SmtpPort,
                (current, next) => current with { SmtpPort = next, EmailPassword = string.Empty }, true);
        }
        /// <summary>
        /// SMTP服务使用SSL
        /// </summary>
        public bool SmtpUseSSL
        {
            get => CaptureCredentialSnapshot().SmtpUseSSL;
            set => SetCredentialProperty(value, nameof(SmtpUseSSL), current => current.SmtpUseSSL,
                (current, next) => current with { SmtpUseSSL = next, EmailPassword = string.Empty }, true);
        }

        private SmtpCredentialSnapshot credentialSnapshot = new(string.Empty, string.Empty, "smtp.qq.com", 587, true);
        private readonly object credentialGate = new();
        private long credentialDestinationVersion;

        internal SmtpCredentialSnapshot CaptureCredentialSnapshot() => Volatile.Read(ref credentialSnapshot);

        internal void RestoreCredentialSnapshot(SmtpCredentialSnapshot snapshot)
        {
            lock (credentialGate)
            {
                string[] properties = [nameof(EmailAddress), nameof(SmtpAddress), nameof(SmtpPort), nameof(SmtpUseSSL), nameof(EmailPassword)];
                foreach (string property in properties) OnPropertyChanging(property);
                Volatile.Write(ref credentialSnapshot, snapshot);
                long committedVersion = ++credentialDestinationVersion;
                foreach (string property in properties[..^1]) OnPropertyChanged(property);
                if (credentialDestinationVersion == committedVersion)
                    Volatile.Write(ref credentialSnapshot, credentialSnapshot with { EmailPassword = snapshot.EmailPassword });
                OnPropertyChanged(nameof(EmailPassword));
            }
        }

        private void SetCredentialProperty<T>(T value, string propertyName,
            Func<SmtpCredentialSnapshot, T> read,
            Func<SmtpCredentialSnapshot, T, SmtpCredentialSnapshot> update,
            bool clearsCredential = false)
        {
            lock (credentialGate)
            {
                if (EqualityComparer<T>.Default.Equals(read(credentialSnapshot), value)) return;
                bool notifyPassword = clearsCredential && credentialSnapshot.EmailPassword.Length != 0;
                long boundVersion = credentialDestinationVersion;
                OnPropertyChanging(propertyName);
                if (notifyPassword) OnPropertyChanging(nameof(EmailPassword));
                if (!clearsCredential && boundVersion != credentialDestinationVersion) return;
                Volatile.Write(ref credentialSnapshot, update(credentialSnapshot, value));
                if (clearsCredential) credentialDestinationVersion++;
                OnPropertyChanged(propertyName);
                if (notifyPassword) OnPropertyChanged(nameof(EmailPassword));
            }
        }
        /// <summary>
        /// 重翻失败部分
        /// </summary>
        [ObservableProperty]
        private bool translateFailedAgain = false;
        /// <summary>
        /// 重翻失败部分最大次数
        /// </summary>
        [ObservableProperty]
        private int translateFailedTimes = 3;
        /// <summary>
        /// 启用翻译完成自动关机
        /// </summary>
        [ObservableProperty]
        private bool autoShutdown;

        public void Enable()
        {
            var snapshot = CaptureCredentialSnapshot();
            ViewModel_SetView vm = ViewModelManager.ViewModel.SetView_ViewModel;
            vm.DefaultTemplate_MTool = DefaultTemplate_MTool;
            vm.DefaultTemplate_Tpp = DefaultTemplate_Tpp;
            vm.DefaultTemplate_Srt = DefaultTemplate_Srt;
            vm.DefaultTemplate_Txt = DefaultTemplate_Txt;
            vm.EnableEmail = EnableEmail;
            vm.RestoreCredentialSnapshot(snapshot);
            vm.AutoShutdown = AutoShutdown;
            vm.TranslateFailedAgain = TranslateFailedAgain;
            vm.TranslateFailedTimes = TranslateFailedTimes;
            ViewModelManager.SaveBaseConfig();
        }

        public static ViewModel_SetView Create()
        {
            ViewModel_SetView vm = ViewModelManager.ViewModel.SetView_ViewModel;
            var snapshot = vm.CaptureCredentialSnapshot();
            return new ViewModel_SetView
            {
                DefaultTemplate_MTool = vm.DefaultTemplate_MTool,
                DefaultTemplate_Tpp = vm.DefaultTemplate_Tpp,
                DefaultTemplate_Srt = vm.DefaultTemplate_Srt,
                DefaultTemplate_Txt = vm.DefaultTemplate_Txt,
                EnableEmail = vm.EnableEmail,
                EmailAddress = snapshot.EmailAddress,
                SmtpAddress = snapshot.SmtpAddress,
                SmtpPort = snapshot.SmtpPort,
                AutoShutdown = vm.AutoShutdown,
                SmtpUseSSL = snapshot.SmtpUseSSL,
                EmailPassword = snapshot.EmailPassword,
                TranslateFailedAgain = vm.TranslateFailedAgain,
                TranslateFailedTimes = vm.TranslateFailedTimes,
            };
        }

        [RelayCommand]
        private void SendTestMail()
        {
            bool result = SmtpMailSender.SendTest(CaptureCredentialSnapshot(), out string error);
            if (!result)
                Window_Message.ShowDialog("错误", $"发送测试邮件失败:{error}" + Environment.NewLine + "请检查网络是否通畅，或输入信息是否有误");
        }
    }

    internal sealed record SmtpCredentialSnapshot(string EmailAddress, string EmailPassword,
        string SmtpAddress, ushort SmtpPort, bool SmtpUseSSL)
    {
        public override string ToString() => "SMTP settings (credential redacted)";
    }
}
