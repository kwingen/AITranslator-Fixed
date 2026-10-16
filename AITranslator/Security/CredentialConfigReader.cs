using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using AITranslator.Translator.Models;
using System;
using System.Collections.Generic;
using System.Reflection;

namespace AITranslator.Security
{
    /// <summary>Keep credential failures as log-safe JSON errors, including Newtonsoft callback failures.</summary>
    internal sealed class CredentialConfigReader : JsonConverter
    {
        public override bool CanWrite => false;
        public override bool CanConvert(Type objectType) => true;

        public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            object value = existingValue ?? Activator.CreateInstance(objectType, nonPublic: true)!;
            try
            {
                var document = JObject.Load(reader, new JsonLoadSettings
                {
                    DuplicatePropertyNameHandling = DuplicatePropertyNameHandling.Error
                });
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in document.Properties())
                    if (!names.Add(property.Name))
                        throw new CredentialProtectionException();

                bool api = objectType == typeof(ConfigSave_Communicator);
                var protectedField = document.GetValue(api ? "CredentialId" : "ProtectedEmailPassword",
                    StringComparison.OrdinalIgnoreCase);
                var legacyField = document.GetValue(api ? "ApiKey" : "EmailPassword",
                    StringComparison.OrdinalIgnoreCase);
                if (protectedField != null && (legacyField != null || protectedField.Type != JTokenType.String ||
                    string.IsNullOrEmpty(protectedField.Value<string>())))
                    throw new CredentialProtectionException();
                if (legacyField != null && legacyField.Type != JTokenType.String && legacyField.Type != JTokenType.Null)
                    throw new CredentialProtectionException();

                // Populate does not invoke a root converter, so all the normal model properties still apply.
                using var safeReader = document.CreateReader();
                serializer.Populate(safeReader, value);
                return value;
            }
            catch (TargetInvocationException error) when (error.InnerException is CredentialProtectionException)
            {
                throw new CredentialProtectionException();
            }
            catch (JsonException)
            {
                // Newtonsoft's syntax/conversion messages can echo input values. Never forward them.
                throw new CredentialProtectionException();
            }
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) =>
            throw new NotSupportedException();
    }
}
