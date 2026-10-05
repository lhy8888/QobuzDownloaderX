using System;
using System.Text;

namespace QobuzDownloaderX.Helpers
{
    internal static class CredentialProtection
    {
        internal static string Encrypt(string value, Func<byte[], byte[]> protect)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            try { return Convert.ToBase64String(protect(Encoding.UTF8.GetBytes(value))); }
            catch (Exception) { return string.Empty; } // Never fall back to the unprotected input.
        }
    }
}
