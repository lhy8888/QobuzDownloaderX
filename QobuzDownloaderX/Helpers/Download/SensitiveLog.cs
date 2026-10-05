using System;
using System.Text.RegularExpressions;

namespace QobuzDownloaderX.Helpers
{
    internal static class SensitiveLog
    {
        internal static string Redact(string message, params string[] secrets)
        {
            if (string.IsNullOrEmpty(message)) return message;
            foreach (string value in secrets)
            {
                if (string.IsNullOrEmpty(value)) continue;
                message = message.Replace(value, "[redacted]").Replace(Uri.EscapeDataString(value), "[redacted]");
            }
            message = Regex.Replace(message, @"https?://[^\s""'<>]+", match =>
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return "[redacted URL]";
                return uri.Scheme + "://" + uri.Host + (uri.IsDefaultPort ? "" : ":" + uri.Port) + "/[redacted]";
            }, RegexOptions.IgnoreCase);
            return Regex.Replace(message, @"((?:password|user_auth_token|app_secret|request_sig|access_token|refresh_token)[""']?\s*[:=]\s*[""']?)[^\s""'&,}]+", "$1[redacted]", RegexOptions.IgnoreCase);
        }
    }
}
