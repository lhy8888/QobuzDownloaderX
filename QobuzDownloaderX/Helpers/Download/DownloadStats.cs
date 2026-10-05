using System.Collections.Generic;
using System.Diagnostics;

namespace QobuzDownloaderX
{
    internal class DownloadStats
    {
        public Stopwatch SpeedWatch { get; set; }
        public long CumulativeBytesRead { get; set; }
        public long LastUiBytes { get; set; }
        public long LastUiTimeMs { get; set; }
        public string LastSpeedText { get; set; } = "";
        public int Succeeded { get; private set; }
        public int Failed { get; private set; }
        public int Skipped { get; private set; }
        public List<string> Failures { get; } = new List<string>();
        public void Success() { Succeeded++; }
        public void Skip() { Skipped++; }
        public void Failure(string id, string message) { Failed++; Failures.Add(id + ": " + message); }
        public string Summary()
        {
            var manager = qbdlxForm._qbdlxForm.languageManager;
            return string.Format(manager.GetTranslation("downloadResultSummary"), Succeeded, Failed, Skipped);
        }
    }
}
