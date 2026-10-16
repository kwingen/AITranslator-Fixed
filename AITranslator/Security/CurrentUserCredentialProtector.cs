using Newtonsoft.Json;
using System;
using System.Security.Cryptography;
using System.Text;

namespace AITranslator.Security
{
    /// <summary>Versioned Windows current-user DPAPI. Never downgrade unreadable data to plaintext.</summary>
    internal static class CurrentUserCredentialProtector
    {
        internal const string Prefix = "dpapi-current-user:v1:";

        internal static string Protect(string secret, string purpose)
        {
            byte[] cleartext = Encoding.UTF8.GetBytes(secret);
            try
            {
                byte[] ciphertext = ProtectedData.Protect(cleartext, Entropy(purpose), DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(ciphertext);
            }
            catch (Exception error) when (error is CryptographicException or PlatformNotSupportedException)
            {
                throw new CredentialProtectionException();
            }
            finally
            {
                CryptographicOperations.ZeroMemory(cleartext);
            }
        }

        internal static string Unprotect(string protectedValue, string purpose)
        {
            if (!protectedValue.StartsWith(Prefix, StringComparison.Ordinal))
                throw new CredentialProtectionException();

            byte[]? cleartext = null;
            try
            {
                byte[] ciphertext = Convert.FromBase64String(protectedValue[Prefix.Length..]);
                cleartext = ProtectedData.Unprotect(ciphertext, Entropy(purpose), DataProtectionScope.CurrentUser);
                return new UTF8Encoding(false, true).GetString(cleartext);
            }
            catch (Exception error) when (error is FormatException or CryptographicException or
                PlatformNotSupportedException or DecoderFallbackException)
            {
                // Do not attach the original exception: data and provider details are not log-safe.
                throw new CredentialProtectionException();
            }
            finally
            {
                if (cleartext != null)
                    CryptographicOperations.ZeroMemory(cleartext);
            }
        }

        private static byte[] Entropy(string purpose) =>
            SHA256.HashData(Encoding.UTF8.GetBytes("AITranslator/credentials/v1/" + purpose));
    }

    /// <summary>Intentionally contains no credential, ciphertext, endpoint or filesystem details.</summary>
    internal sealed class CredentialProtectionException : JsonSerializationException
    {
        internal CredentialProtectionException() : base(
            "A saved credential could not be read or protected for this Windows user and endpoint. " +
            "The original data has not been replaced. Restore access or explicitly re-enter the credential.")
        {
        }
    }
}
