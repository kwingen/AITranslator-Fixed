using AITranslator.Translator.Models;
using AITranslator.Translator.Persistent;
using AITranslator.Security;
using AITranslator.View.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Security.Cryptography;
using System.Text;

namespace AITranslator.Tests;

public sealed class CredentialSafetyTests
{
    [Fact]
    public void PortableLoaderReferenceIsNotASecretBackupAndRequiresTheOriginalLocalStore()
    {
        using var directory = new TemporaryCredentialDirectory();
        var config = new ConfigSave_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = "https://api.example.test/v1",
            ApiKey = "test-key-that-does-not-travel"
        };
        string portable = JsonConvert.SerializeObject(config);
        string otherStore = Path.Combine(directory.Root, "other-user-store");
        Directory.CreateDirectory(otherStore);

        using (LocalCredentialStore.UseDirectoryForTests(otherStore))
        {
            Assert.ThrowsAny<JsonException>(() => JsonConvert.DeserializeObject<ConfigSave_Communicator>(portable));
            Assert.Empty(Directory.GetFiles(otherStore));
            // A deliberately credential-free template remains usable on another profile.
            var credentialFree = JObject.Parse(portable);
            credentialFree.Remove("CredentialId");
            Assert.Empty(JsonConvert.DeserializeObject<ConfigSave_Communicator>(credentialFree.ToString())!.ApiKey);
        }
        Assert.Equal("test-key-that-does-not-travel", JsonConvert.DeserializeObject<ConfigSave_Communicator>(portable)!.ApiKey);
    }

    [Fact]
    public void EmptyCredentialsDoNotCreateProtectedPlaceholdersOrLocalFiles()
    {
        using var directory = new TemporaryCredentialDirectory();
        var api = JObject.Parse(JsonConvert.SerializeObject(new ConfigSave_Communicator()));
        var smtp = JObject.Parse(JsonConvert.SerializeObject(new ConfigSave_Set()));
        Assert.Null(api["ApiKey"]);
        Assert.Null(api["CredentialId"]);
        Assert.Null(smtp["EmailPassword"]);
        Assert.Null(smtp["ProtectedEmailPassword"]);
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public void SmtpEnvelopeCanBeUnprotectedOnlyWithItsBoundDestinationEntropy()
    {
        const string secret = "test-native-smtp-code-授权码";
        var config = new ConfigSave_Set
        {
            EmailAddress = "sender@example.test",
            SmtpAddress = "smtp.example.test",
            SmtpPort = 465,
            SmtpUseSSL = true,
            EmailPassword = secret
        };
        string stored = JObject.Parse(JsonConvert.SerializeObject(config))["ProtectedEmailPassword"]!.Value<string>()!;
        byte[] ciphertext = Convert.FromBase64String(stored[CurrentUserCredentialProtector.Prefix.Length..]);
        string purpose = "smtp-auth:" + JsonConvert.SerializeObject(new object[]
        {
            config.EmailAddress, config.SmtpAddress, config.SmtpPort, config.SmtpUseSSL
        });
        byte[] entropy = SHA256.HashData(Encoding.UTF8.GetBytes("AITranslator/credentials/v1/" + purpose));
        byte[] plaintext = ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
        try
        {
            Assert.Equal(secret, Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        Assert.Throws<CryptographicException>(() => ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void CopyToViewModelSetsDestinationBeforeRestoringCredential(bool api)
    {
        if (api)
        {
            var vm = new ViewModel_Communicator();
            vm.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(vm.ServerURL))
                    vm.ApiKey = string.Empty;
            };
            new ConfigSave_Communicator
            {
                CommunicatorType = CommunicatorType.OpenAI,
                ServerURL = "https://api.example.test/v1",
                ApiKey = "test-copy-api-key"
            }.CopyToViewModel(vm);
            Assert.Equal("test-copy-api-key", vm.ApiKey);
        }
        else
        {
            var vm = new ViewModel_SetView();
            vm.PropertyChanged += (_, change) =>
            {
                if (change.PropertyName == nameof(vm.SmtpAddress) || change.PropertyName == nameof(vm.SmtpPort) ||
                    change.PropertyName == nameof(vm.SmtpUseSSL) || change.PropertyName == nameof(vm.EmailAddress))
                    vm.EmailPassword = string.Empty;
            };
            new ConfigSave_Set
            {
                EmailAddress = "sender@example.test",
                SmtpAddress = "smtp.example.test",
                SmtpPort = 465,
                SmtpUseSSL = true,
                EmailPassword = "test-copy-smtp-code"
            }.CopyToViewModel(vm);
            Assert.Equal("test-copy-smtp-code", vm.EmailPassword);
        }
    }

    [Fact]
    public void MigrationRejectsTrailingJsonBeforeChangingFiles()
    {
        using var directory = new TemporaryCredentialDirectory();
        string path = Path.Combine(directory.Root, "trailing.json");
        const string json = "{\"Set\":{\"EmailPassword\":\"legacy-trailing-test\"}} {\"doNotDiscard\":true}";
        File.WriteAllText(path, json);

        var error = Assert.ThrowsAny<JsonException>(() =>
            CredentialConfigMigration.PrepareForSave(typeof(ConfigSave_Base), path));
        Assert.DoesNotContain("legacy-trailing-test", error.ToString());
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void MigrationRejectsAmbiguousOuterSettingsBeforeChangingFiles()
    {
        using var directory = new TemporaryCredentialDirectory();
        string path = Path.Combine(directory.Root, "ambiguous.json");
        const string json = "{\"Set\":{\"EmailPassword\":\"first-test-value\"},\"set\":{\"EmailPassword\":\"second-test-value\"}}";
        File.WriteAllText(path, json);

        var error = Assert.ThrowsAny<JsonException>(() =>
            CredentialConfigMigration.PrepareForSave(typeof(ConfigSave_Base), path));
        Assert.DoesNotContain("first-test-value", error.ToString());
        Assert.DoesNotContain("second-test-value", error.ToString());
        Assert.Equal(json, File.ReadAllText(path));
    }

    [Fact]
    public void MigrationHelperValidatesEveryCandidateBeforeChangingAnyConfig()
    {
        using var directory = new TemporaryCredentialDirectory();
        string path = Path.Combine(directory.Root, "settings.json");
        string backup = path + ".bak";
        string primary = "{\"Set\":{\"EmailPassword\":\"legacy-safe-test\"},\"UnknownSetting\":{\"keep\":true}}";
        string unreadable = "{\"Set\":{\"ProtectedEmailPassword\":\"dpapi-current-user:v1:corrupted\"}}";
        File.WriteAllText(path, primary);
        File.WriteAllText(backup, unreadable);

        var error = Assert.ThrowsAny<JsonException>(() =>
            CredentialConfigMigration.PrepareForSave(typeof(ConfigSave_Base), path));
        Assert.DoesNotContain("legacy-safe-test", error.ToString());
        Assert.DoesNotContain("corrupted", error.ToString());
        Assert.Equal(primary, File.ReadAllText(path));
        Assert.Equal(unreadable, File.ReadAllText(backup));
    }

    [Fact]
    public void MigrationHelperPreservesUnknownSettingsAndProducesRecoverableBackups()
    {
        using var directory = new TemporaryCredentialDirectory();
        string path = Path.Combine(directory.Root, "settings.json");
        string backup = path + ".bak";
        string json = "{\"Set\":{\"EmailPassword\":\"legacy-migration-test\",\"UnknownSmtpSetting\":42},\"UnknownSetting\":{\"keep\":true}}";
        File.WriteAllText(path, json);
        File.WriteAllText(backup, json);

        CredentialConfigMigration.PrepareForSave(typeof(ConfigSave_Base), path);

        foreach (string candidate in new[] { path, backup })
        {
            var document = JObject.Parse(File.ReadAllText(candidate));
            Assert.Null(document["Set"]!["EmailPassword"]);
            Assert.Equal(42, document["Set"]!["UnknownSmtpSetting"]!.Value<int>());
            Assert.True(document["UnknownSetting"]!["keep"]!.Value<bool>());
            Assert.Equal("legacy-migration-test", JsonPersister.Load<ConfigSave_Base>(candidate).Set.EmailPassword);
        }
        Assert.Empty(Directory.GetFiles(directory.Root, "*.tmp"));
    }

    [Fact]
    public void ApiStoreUsesRealCurrentUserDpapiBoundToReferenceAndEndpoint()
    {
        using var directory = new TemporaryCredentialDirectory();
        const string secret = "test-native-dpapi-key-密钥";
        const string endpoint = "https://api.example.test/v1?deployment=one";
        var config = new ConfigSave_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = endpoint,
            ApiKey = secret
        };
        var document = JObject.Parse(JsonConvert.SerializeObject(config));
        string id = document["CredentialId"]!.Value<string>()!;
        string stored = File.ReadAllText(Path.Combine(directory.Root, id + ".credential"));
        byte[] ciphertext = Convert.FromBase64String(stored[CurrentUserCredentialProtector.Prefix.Length..]);
        byte[] entropy = SHA256.HashData(Encoding.UTF8.GetBytes("AITranslator/credentials/v1/api-key:" + id + ":" + endpoint));
        byte[] plaintext = ProtectedData.Unprotect(ciphertext, entropy, DataProtectionScope.CurrentUser);
        try
        {
            Assert.Equal(secret, Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
        Assert.Throws<CryptographicException>(() =>
            ProtectedData.Unprotect(ciphertext, null, DataProtectionScope.CurrentUser));
    }

    [Theory]
    [InlineData("http://api.example.test/v1")]
    [InlineData("https://attacker.example.test/v1")]
    [InlineData("https://api.example.test:444/v1")]
    [InlineData("https://api.example.test/other")]
    [InlineData("https://api.example.test/v1?deployment=other")]
    public void HandCraftedApiReferenceCannotResolveForAnotherDestination(string endpoint)
    {
        using var directory = new TemporaryCredentialDirectory();
        var config = new ConfigSave_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = "https://api.example.test/v1",
            ApiKey = "test-api-destination-bound-secret"
        };
        var document = JObject.Parse(JsonConvert.SerializeObject(config));
        string storeFile = Assert.Single(Directory.GetFiles(directory.Root));
        byte[] original = File.ReadAllBytes(storeFile);
        document["ServerURL"] = endpoint;
        var error = Assert.ThrowsAny<JsonException>(() =>
            JsonConvert.DeserializeObject<ConfigSave_Communicator>(document.ToString()));
        Assert.DoesNotContain("test-api-destination-bound-secret", error.ToString());
        Assert.Equal(original, File.ReadAllBytes(storeFile));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("malformed")]
    [InlineData("corrupt-dpapi")]
    [InlineData("renamed-reference")]
    public void UnreadableLocalApiCredentialFailsWithoutChangingTheLoaderOrStore(string failure)
    {
        using var directory = new TemporaryCredentialDirectory();
        var config = new ConfigSave_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = "https://api.example.test/v1",
            ApiKey = "test-unreadable-api-key"
        };
        var document = JObject.Parse(JsonConvert.SerializeObject(config));
        string storeFile = Assert.Single(Directory.GetFiles(directory.Root));
        switch (failure)
        {
            case "missing": File.Delete(storeFile); break;
            case "malformed": File.WriteAllText(storeFile, "not-a-valid-protected-value"); break;
            case "corrupt-dpapi":
                File.WriteAllText(storeFile, CurrentUserCredentialProtector.Prefix + Convert.ToBase64String(new byte[64]));
                break;
            case "renamed-reference":
                string newId = Guid.NewGuid().ToString("N");
                File.Move(storeFile, Path.Combine(directory.Root, newId + ".credential"));
                document["CredentialId"] = newId;
                storeFile = Path.Combine(directory.Root, newId + ".credential");
                break;
        }
        byte[]? before = File.Exists(storeFile) ? File.ReadAllBytes(storeFile) : null;
        string path = Path.Combine(directory.Root, "portable.json");
        File.WriteAllText(path, document.ToString());
        byte[] loader = File.ReadAllBytes(path);

        var error = Assert.ThrowsAny<Exception>(() => JsonPersister.Load<ConfigSave_Communicator>(path));
        Assert.DoesNotContain("test-unreadable-api-key", error.ToString());
        Assert.Equal(loader, File.ReadAllBytes(path));
        if (before != null)
            Assert.Equal(before, File.ReadAllBytes(storeFile));
        else
            Assert.False(File.Exists(storeFile));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NormalSaveRefusesToOverwriteAnUnreadableSavedCredential(bool api)
    {
        using var directory = new TemporaryCredentialDirectory();
        string path = Path.Combine(directory.Root, "unreadable.json");
        string unreadable = api ? new JObject
        {
            ["CommunicatorType"] = (int)CommunicatorType.OpenAI,
            ["ServerURL"] = "https://api.example.test/v1",
            ["CredentialId"] = Guid.NewGuid().ToString("N")
        }.ToString() : "{\"Set\":{\"ProtectedEmailPassword\":\"dpapi-current-user:v1:corrupt-test-data\"}}";
        File.WriteAllText(path, unreadable);

        var error = Assert.ThrowsAny<Exception>(() =>
        {
            if (api)
                JsonPersister.Save(new ConfigSave_Communicator(), path);
            else
                JsonPersister.Save(new ConfigSave_Base(), path);
        });
        Assert.DoesNotContain("corrupt-test-data", error.ToString());
        Assert.Equal(unreadable, File.ReadAllText(path));
        Assert.False(File.Exists(path + ".bak"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NormalSaveMigratesLegacyPrimaryAndBothBackupNamesWithoutPlaintext(bool api)
    {
        using var directory = new TemporaryCredentialDirectory();
        string path = Path.Combine(directory.Root, "settings.json");
        string modernBackup = path + ".bak";
        string legacyBackup = Path.Combine(directory.Root, "settings_bak.json");
        const string secret = "legacy-test-secret-that-must-not-survive-in-backups";
        string json = api ? new JObject
        {
            ["CommunicatorType"] = (int)CommunicatorType.OpenAI,
            ["ServerURL"] = "https://api.example.test/v1",
            ["ApiKey"] = secret
        }.ToString() : new JObject
        {
            ["DefaultCommunicator"] = "preserved-template",
            ["Set"] = new JObject
            {
                ["EmailAddress"] = "sender@example.test",
                ["SmtpAddress"] = "smtp.example.test",
                ["EmailPassword"] = secret
            }
        }.ToString();
        File.WriteAllText(path, json);
        File.WriteAllText(modernBackup, json);
        File.WriteAllText(legacyBackup, json);

        if (api)
            JsonPersister.Save(JsonPersister.Load<ConfigSave_Communicator>(path), path);
        else
            JsonPersister.Save(JsonPersister.Load<ConfigSave_Base>(path), path);

        foreach (string candidate in new[] { path, modernBackup, legacyBackup })
        {
            string saved = File.ReadAllText(candidate);
            Assert.DoesNotContain(secret, saved);
            if (api)
                Assert.Equal(secret, JsonPersister.Load<ConfigSave_Communicator>(candidate).ApiKey);
            else
            {
                var loaded = JsonPersister.Load<ConfigSave_Base>(candidate);
                Assert.Equal(secret, loaded.Set.EmailPassword);
                Assert.Equal("preserved-template", loaded.DefaultCommunicator);
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not a URL")]
    [InlineData("file:///C:/credential.txt")]
    [InlineData("https://user:password@api.example.test/v1")]
    [InlineData("https://api.example.test/v1#fragment")]
    public void NonemptyApiKeyRequiresAnUnambiguousHttpEndpoint(string? endpoint)
    {
        using var directory = new TemporaryCredentialDirectory();
        var config = new ConfigSave_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = endpoint!,
            ApiKey = "test-endpoint-validation-key"
        };
        Assert.ThrowsAny<Exception>(() => JsonConvert.SerializeObject(config));
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Fact]
    public void ImportedApiReferenceCannotResolveAsAnotherCommunicatorType()
    {
        using var directory = new TemporaryCredentialDirectory();
        var config = new ConfigSave_Communicator
        {
            CommunicatorType = CommunicatorType.OpenAI,
            ServerURL = "https://api.example.test/v1",
            ApiKey = "test-api-reference-type-key"
        };
        var document = JObject.Parse(JsonConvert.SerializeObject(config));
        document["CommunicatorType"] = (int)CommunicatorType.LLama;
        Assert.ThrowsAny<JsonException>(() => JsonConvert.DeserializeObject<ConfigSave_Communicator>(document.ToString()));
    }

    [Theory]
    [InlineData(false, "{\"ProtectedEmailPassword\":null}")]
    [InlineData(false, "{\"ProtectedEmailPassword\":\"\"}")]
    [InlineData(false, "{\"ProtectedEmailPassword\":\"dpapi-current-user:v1:INVALID\"}")]
    [InlineData(false, "{\"EmailPassword\":123}")]
    [InlineData(false, "{\"EmailPassword\":\"legacy-test\",\"ProtectedEmailPassword\":null}")]
    [InlineData(false, "{\"EmailPassword\":\"first\",\"emailpassword\":\"second\"}")]
    [InlineData(true, "{\"CredentialId\":null}")]
    [InlineData(true, "{\"CredentialId\":\"\"}")]
    [InlineData(true, "{\"CredentialId\":\"../escape\"}")]
    [InlineData(true, "{\"ApiKey\":123}")]
    [InlineData(true, "{\"ApiKey\":\"legacy-test\",\"CredentialId\":null}")]
    [InlineData(true, "{\"ApiKey\":\"first\",\"ApiKey\":\"second\"}")]
    public void MalformedOrAmbiguousCredentialJsonFailsWithoutLoggingItsContents(bool api, string json)
    {
        using var directory = new TemporaryCredentialDirectory();
        var error = Assert.ThrowsAny<JsonException>(() =>
            JsonConvert.DeserializeObject(json, api ? typeof(ConfigSave_Communicator) : typeof(ConfigSave_Set)));
        Assert.DoesNotContain("legacy-test", error.ToString());
        Assert.DoesNotContain("INVALID", error.ToString());
        Assert.DoesNotContain("first", error.ToString());
        Assert.DoesNotContain("second", error.ToString());
        Assert.Empty(Directory.GetFiles(directory.Root));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RestoredCredentialCannotBeReadOrSavedAfterDestinationChanges(bool api)
    {
        using var directory = new TemporaryCredentialDirectory();
        if (api)
        {
            var config = new ConfigSave_Communicator
            {
                CommunicatorType = CommunicatorType.OpenAI,
                ServerURL = "https://api.example.test/v1",
                ApiKey = "test-bound-api-key"
            };
            config = JsonConvert.DeserializeObject<ConfigSave_Communicator>(JsonConvert.SerializeObject(config))!;
            config.ServerURL = "https://attacker.example.test/v1";
            Assert.ThrowsAny<Exception>(() => _ = config.ApiKey);
            Assert.ThrowsAny<Exception>(() => JsonConvert.SerializeObject(config));
        }
        else
        {
            var config = new ConfigSave_Set
            {
                EmailAddress = "sender@example.test",
                SmtpAddress = "smtp.example.test",
                EmailPassword = "test-bound-smtp-code"
            };
            config = JsonConvert.DeserializeObject<ConfigSave_Set>(JsonConvert.SerializeObject(config))!;
            config.SmtpAddress = "smtp.attacker.example.test";
            Assert.ThrowsAny<Exception>(() => _ = config.EmailPassword);
            Assert.ThrowsAny<Exception>(() => JsonConvert.SerializeObject(config));
        }
    }

    [Fact]
    public void LegacyApiKeyIsReadWithoutModifyingInputThenMigratedOnSave()
    {
        using var directory = new TemporaryCredentialDirectory();
        const string secret = "legacy-api-key-for-regression-tests";
        string legacy = new JObject
        {
            ["CommunicatorType"] = (int)CommunicatorType.OpenAI,
            ["ServerURL"] = "https://api.example.test/v1",
            ["Model"] = "test-model",
            ["ApiKey"] = secret
        }.ToString();

        var config = JsonConvert.DeserializeObject<ConfigSave_Communicator>(legacy)!;
        Assert.Equal(secret, config.ApiKey);
        Assert.Empty(Directory.GetFiles(directory.Root));

        string migrated = JsonConvert.SerializeObject(config);
        Assert.DoesNotContain(secret, migrated);
        Assert.Null(JObject.Parse(migrated)["ApiKey"]);
        Assert.Single(Directory.GetFiles(directory.Root));
        Assert.Equal(secret, JsonConvert.DeserializeObject<ConfigSave_Communicator>(migrated)!.ApiKey);
    }

    [Fact]
    public void ApiKeyJsonRoundTripStoresOnlyAnOpaqueReferenceInPortableLoader()
    {
        using var directory = new TemporaryCredentialDirectory();
        const string secret = "sk-test-only-not-a-real-api-key";
        var config = new ConfigSave_Communicator
        {
            CommunicatorType = AITranslator.View.Models.CommunicatorType.OpenAI,
            ServerURL = "https://api.example.test/v1",
            Model = "test-model",
            ApiKey = secret
        };

        string json = JsonConvert.SerializeObject(config);
        var document = JObject.Parse(json);
        Assert.DoesNotContain(secret, json);
        Assert.DoesNotContain("dpapi-current-user:", json);
        Assert.Null(document["ApiKey"]);
        Assert.Matches("^[a-f0-9]{32}$", document["CredentialId"]!.Value<string>()!);
        string stored = File.ReadAllText(Assert.Single(Directory.GetFiles(directory.Root)));
        Assert.DoesNotContain(secret, stored);
        Assert.StartsWith("dpapi-current-user:v1:", stored);
        Assert.Equal(secret, JsonConvert.DeserializeObject<ConfigSave_Communicator>(json)!.ApiKey);
    }

    private sealed class TemporaryCredentialDirectory : IDisposable
    {
        public string Root { get; } = Path.Combine(
            Environment.GetEnvironmentVariable("TMPDIR") ?? Path.GetTempPath(),
            "AITranslator-Credentials-" + Guid.NewGuid().ToString("N"));
        private readonly IDisposable scope;

        public TemporaryCredentialDirectory()
        {
            Directory.CreateDirectory(Root);
            scope = LocalCredentialStore.UseDirectoryForTests(Root);
        }

        public void Dispose()
        {
            scope.Dispose();
            Directory.Delete(Root, recursive: true);
        }
    }

    [Theory]
    [InlineData("EmailAddress", "attacker@example.test")]
    [InlineData("SmtpAddress", "smtp.attacker.test")]
    [InlineData("SmtpPort", 2525)]
    [InlineData("SmtpUseSSL", false)]
    public void ProtectedSmtpCredentialCannotBeReboundByEditingJson(string property, object value)
    {
        var config = new ConfigSave_Set
        {
            EmailAddress = "sender@example.test",
            SmtpAddress = "smtp.example.test",
            SmtpPort = 465,
            SmtpUseSSL = true,
            EmailPassword = "test-smtp-bound-secret"
        };
        var document = JObject.Parse(JsonConvert.SerializeObject(config));
        document[property] = JToken.FromObject(value);

        var error = Assert.ThrowsAny<JsonException>(() =>
            JsonConvert.DeserializeObject<ConfigSave_Set>(document.ToString()));
        Assert.DoesNotContain("test-smtp-bound-secret", error.ToString());
    }

    [Fact]
    public void LegacySmtpPlaintextIsReadThenMigratedByTheNextJsonSave()
    {
        const string secret = "legacy-smtp-auth-code-not-a-real-secret";
        string legacy = new JObject
        {
            ["Set"] = new JObject
            {
                ["EmailAddress"] = "sender@example.test",
                ["SmtpAddress"] = "smtp.example.test",
                ["SmtpPort"] = 465,
                ["SmtpUseSSL"] = true,
                ["EmailPassword"] = secret
            }
        }.ToString();

        var config = JsonConvert.DeserializeObject<ConfigSave_Base>(legacy)!;
        Assert.Equal(secret, config.Set.EmailPassword);

        string migrated = JsonConvert.SerializeObject(config, new JsonSerializerSettings
        {
            DefaultValueHandling = DefaultValueHandling.Ignore
        });
        Assert.DoesNotContain(secret, migrated);
        Assert.Null(JObject.Parse(migrated)["Set"]!["EmailPassword"]);
        Assert.Equal(secret, JsonConvert.DeserializeObject<ConfigSave_Base>(migrated)!.Set.EmailPassword);
    }

    [Fact]
    public void SmtpCredentialJsonRoundTripDoesNotPersistPlaintext()
    {
        const string secret = "smtp-test-auth-code-not-a-real-secret";
        var config = new ConfigSave_Set
        {
            EmailAddress = "sender@example.test",
            SmtpAddress = "smtp.example.test",
            SmtpPort = 465,
            SmtpUseSSL = true,
            EmailPassword = secret
        };

        string json = JsonConvert.SerializeObject(config);

        Assert.DoesNotContain(secret, json);
        Assert.Null(JObject.Parse(json)["EmailPassword"]);
        Assert.Equal(secret, JsonConvert.DeserializeObject<ConfigSave_Set>(json)!.EmailPassword);
    }
}
