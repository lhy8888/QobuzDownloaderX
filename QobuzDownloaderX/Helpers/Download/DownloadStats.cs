using QobuzDownloaderX.Helpers;
using System.Collections.Generic;
using System.Diagnostics;

namespace QobuzDownloaderX
{
    internal class DownloadStats : System.IDisposable
    {
        internal ArtworkCache Artwork { get; } = new ArtworkCache();
        public int SkippedByUser { get; private set; }
        public void SkipByUser(int count) { SkippedByUser += count; }
        public void Dispose() { Artwork.Dispose(); }
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
            string summary = string.Format(manager.GetTranslation("downloadResultSummary"), Succeeded, Failed, Skipped);
            return SkippedByUser == 0 ? summary : summary + " | " + string.Format(manager.GetTranslation("downloadResultUserSkipped"), SkippedByUser);
        }
    }
}
