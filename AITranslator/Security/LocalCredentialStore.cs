using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace AITranslator.Security
{
    /// <summary>
    /// API secrets are local to this Windows profile, never part of shareable loader JSON.
    /// Each opaque reference is cryptographically bound to its complete destination endpoint.
    /// </summary>
    internal static class LocalCredentialStore
    {
        private static readonly AsyncLocal<string?> TestDirectory = new();

        private static string Root => TestDirectory.Value ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AITranslator", "Credentials");

        internal static string Save(string secret, string endpoint)
        {
            string id = Guid.NewGuid().ToString("N");
            string protectedValue = CurrentUserCredentialProtector.Protect(secret, Purpose(id, endpoint));
            try
            {
                Directory.CreateDirectory(Root);
                // Never replace an existing credential. Failed writes cannot damage an old reference.
                using var file = new FileStream(PathFor(id), FileMode.CreateNew, FileAccess.Write, FileShare.None);
                byte[] bytes = Encoding.UTF8.GetBytes(protectedValue);
                file.Write(bytes);
                file.Flush(flushToDisk: true);
                return id;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new CredentialProtectionException();
            }
        }

        internal static string Load(string id, string endpoint)
        {
            try
            {
                string protectedValue = File.ReadAllText(PathFor(id));
                return CurrentUserCredentialProtector.Unprotect(protectedValue, Purpose(id, endpoint));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                throw new CredentialProtectionException();
            }
        }

        private static string PathFor(string id)
        {
            if (id.Length != 32 || !id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new CredentialProtectionException();
            return Path.Combine(Root, id + ".credential");
        }

        private static string Purpose(string id, string endpoint) => "api-key:" + id + ":" + endpoint;

        // Async-local and scoped so tests never read/write real user credentials or affect other threads.
        internal static IDisposable UseDirectoryForTests(string directory)
        {
            string? previous = TestDirectory.Value;
            TestDirectory.Value = Path.GetFullPath(directory);
            return new DirectoryScope(previous);
        }

        private sealed class DirectoryScope(string? previous) : IDisposable
        {
            public void Dispose() => TestDirectory.Value = previous;
        }
    }
}
