using AITranslator.Translator.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace AITranslator.Security
{
    /// <summary>
    /// Invoke only on an explicit app-owned config save, never on imports or read-only loads.
    /// Validate every recovery candidate first, then replace plaintext credentials with recoverable
    /// protected equivalents before the checkpoint writer rotates the previous primary into a backup.
    /// </summary>
    internal static class CredentialConfigMigration
    {
        internal static void PrepareForSave(Type configurationType, string filePath)
        {
            if (configurationType != typeof(ConfigSave_Communicator) &&
                configurationType != typeof(ConfigSave_Set) && configurationType != typeof(ConfigSave_Base))
                return;

            string path = Path.GetFullPath(filePath);
            var replacements = new List<(string Path, string Json)>();
            foreach (string candidate in new[]
            {
                path, path + ".bak",
                Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_bak" + Path.GetExtension(path))
            })
            {
                if (!File.Exists(candidate))
                    continue;

                try
                {
                    using var reader = new JsonTextReader(new StringReader(File.ReadAllText(candidate)));
                    var document = JObject.Load(reader, new JsonLoadSettings
                    {
                        DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                    });
                    while (reader.Read())
                        if (reader.TokenType != JsonToken.Comment)
                            throw new CredentialProtectionException();
                    foreach (var obj in document.DescendantsAndSelf().OfType<JObject>())
                    {
                        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var property in obj.Properties())
                            if (!names.Add(property.Name))
                                throw new CredentialProtectionException();
                    }
                    object config = document.ToObject(configurationType) ?? throw new CredentialProtectionException();
                    JObject? credentials = configurationType == typeof(ConfigSave_Base)
                        ? document.GetValue("Set", StringComparison.OrdinalIgnoreCase) as JObject : document;
                    bool api = configurationType == typeof(ConfigSave_Communicator);
                    string legacyName = api ? "ApiKey" : "EmailPassword";
                    if (credentials?.GetValue(legacyName, StringComparison.OrdinalIgnoreCase) == null)
                        continue;

                    // Keep all unrelated/unknown settings exactly as JSON values, not just known model fields.
                    var safeDocument = JObject.FromObject(config);
                    JObject safeCredentials = configurationType == typeof(ConfigSave_Base)
                        ? (JObject)safeDocument["Set"]! : safeDocument;
                    foreach (var legacy in credentials.Properties().Where(p =>
                        string.Equals(p.Name, legacyName, StringComparison.OrdinalIgnoreCase)).ToArray())
                        legacy.Remove();
                    string protectedName = api ? "CredentialId" : "ProtectedEmailPassword";
                    if (safeCredentials[protectedName] is JToken protectedValue)
                        credentials[protectedName] = protectedValue.DeepClone();
                    replacements.Add((candidate, document.ToString(Formatting.Indented)));
                }
                catch (JsonException)
                {
                    // Do not forward parser messages, which can contain the legacy plaintext value.
                    throw new CredentialProtectionException();
                }
            }

            // No source/recovery file is changed until every candidate was read and validated.
            foreach (var replacement in replacements)
                ReplaceWithoutPlaintextBackup(replacement.Path, replacement.Json);
        }

        private static void ReplaceWithoutPlaintextBackup(string path, string json)
        {
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".credential-migration.tmp";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                    4096, FileOptions.WriteThrough))
                {
                    byte[] bytes = new UTF8Encoding(false).GetBytes(json);
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                // The replacement is semantically equivalent and recoverable; never back up the plaintext.
                File.Replace(temporary, path, destinationBackupFileName: null);
            }
            finally
            {
                try { File.Delete(temporary); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }
}
