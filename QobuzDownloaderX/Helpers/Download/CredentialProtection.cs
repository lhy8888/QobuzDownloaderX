using System;
using System.Text;

namespace QobuzDownloaderX.Helpers
{
    internal static class CredentialProtection
    {
        internal const string Prefix = "dpapi:v1:";
        private static readonly byte[] LegacyHeader = { 1, 0, 0, 0, 0xd0, 0x8c, 0x9d, 0xdf, 1, 0x15, 0xd1, 0x11, 0x8c, 0x7a, 0, 0xc0, 0x4f, 0xc2, 0x97, 0xeb };
        internal static string Encrypt(string value, Func<byte[], byte[]> protect)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            try { return Prefix + Convert.ToBase64String(protect(Encoding.UTF8.GetBytes(value))); }
            catch (Exception) { return string.Empty; } // Never fall back to the unprotected input.
        }

        internal static string Read(string stored, Func<byte[], byte[]> protect, Func<byte[], byte[]> unprotect, out string replacement)
        {
            replacement = stored ?? string.Empty;
            if (string.IsNullOrEmpty(stored)) return string.Empty;
            bool tagged = stored.StartsWith(Prefix, StringComparison.Ordinal);
            byte[] data = null;
            try { data = Convert.FromBase64String(tagged ? stored.Substring(Prefix.Length) : stored); }
            catch (FormatException) { }
            bool legacyProtected = data != null && data.Length >= LegacyHeader.Length;
            for (int i = 0; legacyProtected && i < LegacyHeader.Length; i++) legacyProtected = data[i] == LegacyHeader[i];
            if (tagged || legacyProtected)
            {
                try
                {
                    if (data == null) throw new FormatException();
                    string value = Encoding.UTF8.GetString(unprotect(data));
                    replacement = Prefix + Convert.ToBase64String(data);
                    return value;
                }
                catch (Exception) { replacement = string.Empty; return string.Empty; }
            }
            // A plaintext password/key may itself be valid Base64. Only the
            // explicit marker or actual DPAPI blob header identifies ciphertext.
            replacement = Encrypt(stored, protect);
            return stored;
        }
    }
}
