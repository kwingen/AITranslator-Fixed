using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using AITranslator.View.Models;
using static LLama.Common.ChatHistory;

namespace AITranslator.Mail
{
    public static class SmtpMailSender
    {
        public static bool SendFail(string taskName)
        {
            string title = "[AITranslator]翻译任务失败";
            string message = $"您的的翻译任务[{taskName}]出现故障，翻译暂停，请打开电脑查看，如您未使用此软件，请立刻检查自身SMTP权限授权码是否泄漏！";
            return Send(title, message, out string error);
        }
        public static bool SendSuccess(string taskName)
        {
            string title = "[AITranslator]翻译任务完成";
            string message = $"您的的翻译任务[{taskName}]已完成,请打开电脑查看，如您未使用此软件，请立刻检查自身SMTP权限授权码是否泄漏！";
            return Send(title, message, out string error);
        }
        public static bool SendTest(string mailAddress, string mailPassword, string smtpAddress, ushort smtpPort, bool useSSL, out string error)
        {
            string title = "[AITranslator]测试邮件";
            string message = "此邮件为AITranslator的测试邮件，如您未使用此软件，请立刻检查自身SMTP权限授权码是否泄漏！";
            return Send(title, message,
                mailAddress,
                mailPassword,
                smtpAddress,
                smtpPort,
                useSSL,
                out error);
        }
        public static bool Send(string title, string Message, out string error)
        {
            ViewModel_SetView vm = ViewModelManager.ViewModel.SetView_ViewModel;
            return Send(title, Message, vm.CaptureCredentialSnapshot(), out error);
        }

        public static bool Send(string title, string Message, string mailAddress, string mailPassword, string smtpAddress, ushort smtpPort, bool useSSL, out string error)
            => Send(title, Message, new SmtpCredentialSnapshot(mailAddress, mailPassword, smtpAddress, smtpPort, useSSL), out error);

        internal static bool SendTest(SmtpCredentialSnapshot snapshot, out string error)
            => Send("[AITranslator]测试邮件", "此邮件为AITranslator的测试邮件，如您未使用此软件，请立刻检查自身SMTP权限授权码是否泄漏！", snapshot, out error);

        // The transport callback is a test boundary: production always uses SmtpClient.Send.
        internal static bool Send(string title, string message, SmtpCredentialSnapshot snapshot, out string error,
            Action<SmtpClient, MailMessage>? transmit = null)
        {
            error = string.Empty;
            if (string.IsNullOrEmpty(snapshot.EmailPassword))
            {
                error = "Re-enter the SMTP authorization code for this destination before sending.";
                return false;
            }
            try
            {
                using var mail = new MailMessage(snapshot.EmailAddress, snapshot.EmailAddress)
                {
                    Subject = title,
                    Body = message
                };
                using var smtpClient = new SmtpClient(snapshot.SmtpAddress)
                {
                    Timeout = 3000,
                    Port = snapshot.SmtpPort,
                    Credentials = new NetworkCredential(snapshot.EmailAddress, snapshot.EmailPassword),
                    EnableSsl = snapshot.SmtpUseSSL
                };
                if (transmit == null) smtpClient.Send(mail);
                else transmit(smtpClient, mail);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

    }
}
