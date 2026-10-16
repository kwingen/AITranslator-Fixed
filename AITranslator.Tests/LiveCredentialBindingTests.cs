using AITranslator.Mail;
using AITranslator.Security;
using AITranslator.Translator.Translation;
using AITranslator.Translator.Communicator;
using AITranslator.Translator.Models;
using AITranslator.Translator.PostData;
using AITranslator.View.Models;
using Newtonsoft.Json;
using System.Net;
using System.Net.Http;

namespace AITranslator.Tests;

public sealed class LiveCredentialBindingTests
{
    [Fact]
    public void RestoredApiCredentialCannotFollowLiveDestinationEditIntoRequestOrSave()
    {
        using var store = new CredentialDirectory();
        var original = new ConfigSave_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = "https://original.example.test/v1",
            ApiKey = "fake-original-api-key"
        };
        var restored = RoundTrip(original);
        var vm = new ViewModel_Communicator();
        restored.CopyToViewModel(vm);
        Assert.Equal("fake-original-api-key", vm.ApiKey);
        vm.ServerURL = "https://original.example.test/v1";
        vm.CommunicatorType = CommunicatorType.OpenAI;
        var unchanged = new ConfigSave_Communicator();
        unchanged.CopyFromViewModel(vm);
        Assert.Equal("fake-original-api-key", RoundTrip(unchanged).ApiKey);

        vm.ServerURL = "https://edited.example.test/v1";
        using var handler = new RecordingHandler();
        using var client = new HttpClient(handler);
        using var translator = new ManualTranslator(vm, client);
        translator._communicator.Translate(new PostDataBase(), [], [], "test", out _);
        Assert.Equal("https://edited.example.test/v1/chat/completions", handler.Destination!.AbsoluteUri);
        Assert.Null(handler.Authorization);

        var saved = new ConfigSave_Communicator();
        saved.CopyFromViewModel(vm);
        Assert.Empty(RoundTrip(saved).ApiKey);
        var invalidated = vm.CaptureCredentialSnapshot();
        vm.ApiKey = "fake-original-api-key"; // Deliberate re-entry may authorize the new destination.
        Assert.Empty(invalidated.ApiKey); // A previously captured value cannot change underneath a send.
        using var reentered = new ManualTranslator(vm, client);
        reentered._communicator.Translate(new PostDataBase(), [], [], "test", out _);
        Assert.Equal("https://edited.example.test/v1/chat/completions", handler.Destination!.AbsoluteUri);
        Assert.Equal("Bearer fake-original-api-key", handler.Authorization);
        saved.CopyFromViewModel(vm);
        Assert.Equal("fake-original-api-key", RoundTrip(saved).ApiKey);
    }

    [Fact]
    public void ApiTypeChangeAndReturnRequiresExplicitReentry()
    {
        using var store = new CredentialDirectory();
        var vm = new ViewModel_Communicator();
        RoundTrip(new ConfigSave_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = "https://original.example.test/v1",
            ApiKey = "fake-type-bound-key"
        }).CopyToViewModel(vm);
        vm.CommunicatorType = CommunicatorType.LLama;
        vm.CommunicatorType = CommunicatorType.OpenAI;
        Assert.Empty(vm.ApiKey);
        vm.ApiKey = "fake-type-bound-key";
        var saved = new ConfigSave_Communicator();
        saved.CopyFromViewModel(vm);
        Assert.Equal("fake-type-bound-key", RoundTrip(saved).ApiKey);
    }

    [Fact]
    public async Task ConcurrentApiSaveNeverRebindsCredentialToAnotherDestination()
    {
        using var store = new CredentialDirectory();
        var vm = new ViewModel_Communicator { CommunicatorType = CommunicatorType.OpenAI };
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        Task edit = Task.Run(() =>
        {
            int i = 0;
            started.Set();
            while (!stop.IsCancellationRequested)
            {
                string label = (++i % 2 == 0) ? "original" : "edited";
                vm.ServerURL = $"https://{label}.example.test/v1";
                vm.ApiKey = "fake-" + label;
            }
        });
        started.Wait();
        try
        {
            for (int i = 0; i < 20000; i++)
            {
                var captured = new ConfigSave_Communicator();
                captured.CopyFromViewModel(vm);
                if (string.IsNullOrEmpty(captured.ApiKey)) continue;
                string expected = captured.ServerURL.Contains("original.") ? "fake-original" : "fake-edited";
                if (captured.ApiKey != expected)
                {
                    var persisted = RoundTrip(captured);
                    Assert.Equal(expected, persisted.ApiKey);
                }
            }
        }
        finally
        {
            stop.Cancel();
            await edit;
        }
    }

    [Fact]
    public void RejectedApiDestinationEditCannotCreateAPhantomCredentialBinding()
    {
        var vm = new ViewModel_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = "https://original.example.test/v1",
            ApiKey = "fake-original"
        };
        vm.PropertyChanging += (_, change) =>
        {
            if (change.PropertyName == nameof(vm.ServerURL))
                throw new InvalidOperationException("synthetic observer failure");
        };
        Assert.Throws<InvalidOperationException>(() => vm.ServerURL = "https://edited.example.test/v1");
        vm.ApiKey = "fake-reentered-at-visible-destination";
        var captured = new ConfigSave_Communicator();
        captured.CopyFromViewModel(vm);
        Assert.Equal(vm.ServerURL, captured.ServerURL);
        Assert.Equal("https://original.example.test/v1", captured.ServerURL);
    }

    [Theory]
    [InlineData("account")]
    [InlineData("host")]
    [InlineData("port")]
    [InlineData("tls")]
    public void RestoredSmtpCredentialCannotFollowLiveDestinationEditIntoSave(string property)
    {
        var vm = new ViewModel_SetView();
        RoundTrip(new ConfigSave_Set
        {
            EmailAddress = "sender@example.test", SmtpAddress = "smtp.example.test",
            SmtpPort = 465, SmtpUseSSL = true, EmailPassword = "fake-original-smtp-code"
        }).CopyToViewModel(vm);
        Assert.Equal("fake-original-smtp-code", vm.EmailPassword);
        switch (property)
        {
            case "account": vm.EmailAddress = "edited@example.test"; break;
            case "host": vm.SmtpAddress = "smtp.edited.example.test"; break;
            case "port": vm.SmtpPort = 2525; break;
            case "tls": vm.SmtpUseSSL = false; break;
        }
        var saved = new ConfigSave_Set();
        saved.CopyFromViewModel(vm);
        Assert.Empty(RoundTrip(saved).EmailPassword);
        vm.EmailPassword = "fake-reentered-smtp-code";
        saved.CopyFromViewModel(vm);
        Assert.Equal("fake-reentered-smtp-code", RoundTrip(saved).EmailPassword);
    }

    [Fact]
    public void SettingsCloneAndApplyPreserveCredentialForCapturedDestination()
    {
        using var directory = new CredentialDirectory();
        string previousDirectory = Environment.CurrentDirectory;
        var previous = ViewModelManager.ViewModel.SetView_ViewModel;
        try
        {
            Environment.CurrentDirectory = directory.Root;
            var original = new ViewModel_SetView();
            RoundTrip(new ConfigSave_Set
            {
                EmailAddress = "sender@example.test", SmtpAddress = "smtp.example.test",
                SmtpPort = 465, SmtpUseSSL = false, EmailPassword = "fake-clone-smtp-code"
            }).CopyToViewModel(original);
            ViewModelManager.ViewModel.SetView_ViewModel = original;
            var clone = ViewModel_SetView.Create();
            Assert.Equal("fake-clone-smtp-code", clone.EmailPassword);
            clone.SmtpAddress = "smtp.edited.example.test";
            clone.SmtpPort = 2525;
            clone.SmtpUseSSL = true;
            clone.EmailPassword = "fake-edited-smtp-code";
            clone.Enable();
            Assert.Equal("fake-edited-smtp-code", original.EmailPassword);
            var persisted = JsonConvert.DeserializeObject<ConfigSave_Base>(File.ReadAllText(PublicParams.ConfigPath_LoadModel))!;
            Assert.Equal("fake-edited-smtp-code", persisted.Set.EmailPassword);
            Assert.Equal("smtp.edited.example.test", persisted.Set.SmtpAddress);
        }
        finally
        {
            ViewModelManager.ViewModel.SetView_ViewModel = previous;
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Fact]
    public async Task ConcurrentSmtpSaveNeverRebindsCredentialToAnotherDestination()
    {
        var vm = new ViewModel_SetView { EmailAddress = "sender@example.test" };
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        Task edit = Task.Run(() =>
        {
            int i = 0;
            started.Set();
            while (!stop.IsCancellationRequested)
            {
                string label = (++i % 2 == 0) ? "original" : "edited";
                vm.SmtpAddress = $"smtp.{label}.example.test";
                vm.EmailPassword = "fake-" + label;
            }
        });
        started.Wait();
        try
        {
            for (int i = 0; i < 20000; i++)
            {
                var captured = new ConfigSave_Set();
                captured.CopyFromViewModel(vm);
                if (string.IsNullOrEmpty(captured.EmailPassword)) continue;
                string expected = captured.SmtpAddress.Contains("original.") ? "fake-original" : "fake-edited";
                if (captured.EmailPassword != expected)
                    Assert.Equal(expected, RoundTrip(captured).EmailPassword);
            }
        }
        finally
        {
            stop.Cancel();
            await edit;
        }
    }

    [Fact]
    public void SettingsApplyCapturesCredentialsBeforeDestinationObserversRun()
    {
        using var directory = new CredentialDirectory();
        string previousDirectory = Environment.CurrentDirectory;
        var previous = ViewModelManager.ViewModel.SetView_ViewModel;
        var source = new ViewModel_SetView
        {
            EmailAddress = "original@example.test", SmtpAddress = "smtp.original.example.test",
            SmtpPort = 465, SmtpUseSSL = true, EmailPassword = "fake-original"
        };
        var target = new ViewModel_SetView();
        target.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName != nameof(target.EmailAddress)) return;
            source.EmailAddress = "edited@example.test";
            source.SmtpAddress = "smtp.edited.example.test";
            source.EmailPassword = "fake-edited";
        };
        try
        {
            Environment.CurrentDirectory = directory.Root;
            ViewModelManager.ViewModel.SetView_ViewModel = target;
            source.Enable();
            Assert.Equal("original@example.test", target.EmailAddress);
            Assert.Equal("smtp.original.example.test", target.SmtpAddress);
            Assert.Equal("fake-original", target.EmailPassword);
        }
        finally
        {
            ViewModelManager.ViewModel.SetView_ViewModel = previous;
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CredentialEntryCannotBeReboundByReentrantDestinationEdit(bool api)
    {
        if (api)
        {
            var vm = new ViewModel_Communicator
            {
                CommunicatorType = CommunicatorType.OpenAI,
                ServerURL = "https://original.example.test/v1"
            };
            vm.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(vm.ApiKey)) vm.ServerURL = "https://edited.example.test/v1";
            };
            vm.ApiKey = "fake-entered-for-original";
            Assert.Empty(vm.CaptureCredentialSnapshot().ApiKey);
        }
        else
        {
            var vm = new ViewModel_SetView { SmtpAddress = "smtp.original.example.test" };
            vm.PropertyChanging += (_, change) =>
            {
                if (change.PropertyName == nameof(vm.EmailPassword)) vm.SmtpAddress = "smtp.edited.example.test";
            };
            vm.EmailPassword = "fake-entered-for-original";
            Assert.Empty(vm.CaptureCredentialSnapshot().EmailPassword);
        }
    }

    [Fact]
    public async Task ConcurrentSmtpCloneNeverRebindsCredentialToAnotherDestination()
    {
        var previous = ViewModelManager.ViewModel.SetView_ViewModel;
        var vm = new ViewModel_SetView();
        ViewModelManager.ViewModel.SetView_ViewModel = vm;
        using var stop = new CancellationTokenSource();
        using var started = new ManualResetEventSlim();
        Task edit = Task.Run(() =>
        {
            int i = 0;
            while (!stop.IsCancellationRequested)
            {
                bool original = ++i % 2 == 0;
                string label = original ? "original" : "edited";
                vm.EmailAddress = label + "@example.test";
                vm.SmtpAddress = $"smtp.{label}.example.test";
                vm.SmtpPort = (ushort)(original ? 465 : 2525);
                vm.SmtpUseSSL = original;
                vm.EmailPassword = "fake-" + label;
                started.Set();
            }
        });
        started.Wait();
        try
        {
            for (int i = 0; i < 20000; i++)
            {
                var clone = ViewModel_SetView.Create();
                if (clone.EmailPassword.Length == 0) continue;
                string label = clone.SmtpAddress.Contains("original.") ? "original" : "edited";
                Assert.Equal(label + "@example.test", clone.EmailAddress);
                Assert.Equal("fake-" + label, clone.EmailPassword);
                Assert.Equal(label == "original" ? 465 : 2525, clone.SmtpPort);
                Assert.Equal(label == "original", clone.SmtpUseSSL);
            }
        }
        finally
        {
            stop.Cancel();
            await edit;
            ViewModelManager.ViewModel.SetView_ViewModel = previous;
        }
    }

    [Fact]
    public void SmtpDestinationEditRequiresReentryBeforeTransportCanRun()
    {
        var vm = new ViewModel_SetView();
        RoundTrip(new ConfigSave_Set
        {
            EmailAddress = "sender@example.test", SmtpAddress = "smtp.original.example.test",
            SmtpPort = 465, SmtpUseSSL = true, EmailPassword = "fake-original-smtp-code"
        }).CopyToViewModel(vm);
        vm.SmtpAddress = "smtp.edited.example.test";
        bool transmitted = false;
        bool result = SmtpMailSender.Send("test", "test", vm.CaptureCredentialSnapshot(), out string error,
            (_, _) => transmitted = true);
        Assert.False(result);
        Assert.False(transmitted);
        Assert.DoesNotContain("fake-original-smtp-code", error);
        vm.EmailPassword = "fake-reentered-smtp-code";
        result = SmtpMailSender.Send("test", "test", vm.CaptureCredentialSnapshot(), out error,
            (client, mail) =>
            {
                transmitted = true;
                Assert.Equal("smtp.edited.example.test", client.Host);
                Assert.Equal("fake-reentered-smtp-code", Assert.IsType<NetworkCredential>(client.Credentials).Password);
                Assert.Equal("sender@example.test", mail.From!.Address);
                Assert.Equal(465, client.Port);
                Assert.True(client.EnableSsl);
            });
        Assert.True(result, error);
        Assert.True(transmitted);
    }

    [Fact]
    public void ApiConfigRestoreCannotRebindSavedKeyThroughDestinationObserver()
    {
        using var store = new CredentialDirectory();
        var restored = RoundTrip(new ConfigSave_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = "https://original.example.test/v1", ApiKey = "fake-original-bound-key"
        });
        var vm = new ViewModel_Communicator();
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(vm.ServerURL) && vm.ServerURL == restored.ServerURL)
                vm.ServerURL = "https://edited.example.test/v1";
        };
        restored.CopyToViewModel(vm);
        var captured = vm.CaptureCredentialSnapshot();
        Assert.Equal("https://edited.example.test/v1", captured.ServerURL);
        Assert.Empty(captured.ApiKey);
    }

    [Fact]
    public void SmtpConfigRestoreCannotRebindSavedPasswordThroughDestinationObserver()
    {
        var restored = RoundTrip(new ConfigSave_Set
        {
            EmailAddress = "sender@example.test", SmtpAddress = "smtp.original.example.test",
            SmtpPort = 465, SmtpUseSSL = true, EmailPassword = "fake-original-bound-password"
        });
        var vm = new ViewModel_SetView();
        vm.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(vm.SmtpAddress) && vm.SmtpAddress == restored.SmtpAddress)
                vm.SmtpAddress = "smtp.edited.example.test";
        };
        restored.CopyToViewModel(vm);
        var captured = vm.CaptureCredentialSnapshot();
        Assert.Equal("smtp.edited.example.test", captured.SmtpAddress);
        Assert.Empty(captured.EmailPassword);
    }

    [Fact]
    public void SettingsApplyCannotRebindPasswordThroughTargetDestinationObserver()
    {
        using var directory = new CredentialDirectory();
        string previousDirectory = Environment.CurrentDirectory;
        var previous = ViewModelManager.ViewModel.SetView_ViewModel;
        var source = new ViewModel_SetView
        {
            EmailAddress = "sender@example.test", SmtpAddress = "smtp.original.example.test",
            SmtpPort = 465, SmtpUseSSL = true, EmailPassword = "fake-original-bound-password"
        };
        var target = new ViewModel_SetView();
        target.PropertyChanged += (_, change) =>
        {
            if (change.PropertyName == nameof(target.SmtpAddress) && target.SmtpAddress == "smtp.original.example.test")
                target.SmtpAddress = "smtp.edited.example.test";
        };
        try
        {
            Environment.CurrentDirectory = directory.Root;
            ViewModelManager.ViewModel.SetView_ViewModel = target;
            source.Enable();
            Assert.Equal("smtp.edited.example.test", target.SmtpAddress);
            Assert.Empty(target.EmailPassword);
            var saved = JsonConvert.DeserializeObject<ConfigSave_Base>(File.ReadAllText(PublicParams.ConfigPath_LoadModel))!;
            Assert.Empty(saved.Set.EmailPassword);
        }
        finally
        {
            ViewModelManager.ViewModel.SetView_ViewModel = previous;
            Environment.CurrentDirectory = previousDirectory;
        }
    }

    private static T RoundTrip<T>(T value) =>
        JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(value))!;

    private sealed class RecordingHandler : HttpMessageHandler
    {
        internal string? Authorization { get; private set; }
        internal Uri? Destination { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Authorization = request.Headers.Authorization?.ToString();
            Destination = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"choices\":[{\"finish_reason\":\"stop\",\"message\":{\"content\":\"ok\"}}]}")
            });
        }
    }

    private sealed class CredentialDirectory : IDisposable
    {
        internal string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("TMPDIR") ??
            Path.GetTempPath(), "live-credential-" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable scope;
        internal CredentialDirectory()
        {
            Directory.CreateDirectory(Root);
            scope = LocalCredentialStore.UseDirectoryForTests(Root);
        }
        public void Dispose()
        {
            scope.Dispose();
            Directory.Delete(Root, true);
        }
    }
}
