using System.Security.Cryptography;
using System.Text;

namespace ResourceMonitorDaemon.Observability
{
    /// <summary>Encrypts small secrets for storage in the user settings file.</summary>
    public interface ISecretProtector
    {
        /// <summary>Returns base64 ciphertext for <paramref name="secret"/>.</summary>
        string Protect(string secret);

        /// <summary>Decrypts a value produced by <see cref="Protect"/>; false (never throws) when it cannot.</summary>
        bool TryUnprotect(string protectedValue, out string secret);
    }

    /// <summary>
    /// Windows DPAPI with <see cref="DataProtectionScope.CurrentUser"/>: only the same Windows user
    /// on the same PC can decrypt. <c>System.Security.Cryptography.ProtectedData</c> ships with the
    /// Windows Desktop shared framework (WindowsForms profile), so no extra package is needed.
    /// </summary>
    public sealed class DpapiSecretProtector : ISecretProtector
    {
        // Namespacing only: this constant is public (open source), so any program running as the
        // same Windows user can decrypt the value. DPAPI CurrentUser is the protection boundary
        // (other users and other PCs cannot decrypt it).
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ResourceMonitorDaemon/Observability/Loki/Token/v1");

        public string Protect(string secret)
        {
            ArgumentNullException.ThrowIfNull(secret);
            var plain = Encoding.UTF8.GetBytes(secret);
            try
            {
                return Convert.ToBase64String(ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser));
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }

        public bool TryUnprotect(string protectedValue, out string secret)
        {
            secret = string.Empty;
            try
            {
                var plain = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
                secret = Encoding.UTF8.GetString(plain);
                CryptographicOperations.ZeroMemory(plain);
                return true;
            }
            catch (Exception ex) when (ex is CryptographicException or FormatException)
            {
                return false;
            }
        }
    }
}
