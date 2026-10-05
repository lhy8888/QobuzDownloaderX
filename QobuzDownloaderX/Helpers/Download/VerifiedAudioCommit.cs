using System;
using System.IO;
using System.Threading;

namespace QobuzDownloaderX.Helpers
{
    internal static class VerifiedAudioCommit
    {
        // Hashing and decoding finish before entering this short, synchronous, cross-process critical section.
        internal static string Commit(string stagedAudio, string receiptJson, string desiredPath, bool overwrite, CancellationToken token)
        {
            string folder = Path.GetDirectoryName(Path.GetFullPath(desiredPath));
            string stagedReceipt = Path.Combine(folder, ".qbdlx-receipt-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                AtomicFiles.WriteText(stagedReceipt, receiptJson);
                using (var gate = new Mutex(false, AtomicFiles.LockName(Path.Combine(folder, ".qbdlx-save"))))
                {
                    AtomicFiles.Acquire(gate, token);
                    try
                    {
                        string destination = overwrite ? desiredPath : DuplicatePath(desiredPath);
                        string receipt = destination + AudioVerification.ReceiptExtension;
                        if (Directory.Exists(destination) || Directory.Exists(receipt)) throw new IOException("The audio or verification record destination is a directory.");
                        string backupAudio = Path.Combine(folder, ".qbdlx-backup-" + Guid.NewGuid().ToString("N") + Path.GetExtension(destination));
                        string backupReceipt = Path.Combine(folder, ".qbdlx-backup-" + Guid.NewGuid().ToString("N") + ".json");
                        bool oldAudio = false, oldReceipt = false, newAudio = false, newReceipt = false;
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            if (File.Exists(destination)) { File.Move(destination, backupAudio); oldAudio = true; }
                            if (File.Exists(receipt)) { File.Move(receipt, backupReceipt); oldReceipt = true; }
                            token.ThrowIfCancellationRequested();
                            File.Move(stagedAudio, destination); newAudio = true;
                            File.Move(stagedReceipt, receipt); newReceipt = true;
                        }
                        catch
                        {
                            // Preserve backups if rollback itself fails; never delete the only remaining old copy.
                            try
                            {
                                if (newReceipt) File.Delete(receipt);
                                if (newAudio) File.Move(destination, stagedAudio);
                                if (oldAudio) File.Move(backupAudio, destination);
                                if (oldReceipt) File.Move(backupReceipt, receipt);
                            }
                            catch (Exception rollback) { throw new IOException("Saving failed and recovery was incomplete. Previous files are retained as .qbdlx-backup files in the download directory.", rollback); }
                            throw;
                        }
                        AtomicFiles.TryDelete(backupAudio); AtomicFiles.TryDelete(backupReceipt);
                        return destination;
                    }
                    finally { gate.ReleaseMutex(); }
                }
            }
            finally { AtomicFiles.TryDelete(stagedReceipt); }
        }
        private static string DuplicatePath(string path)
        {
            if (!File.Exists(path) && !File.Exists(path + AudioVerification.ReceiptExtension)) return path;
            for (int n = 1; n <= 100000; n++)
            {
                string candidate = Path.Combine(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path) + " (" + n + ")" + Path.GetExtension(path));
                if (!File.Exists(candidate) && !File.Exists(candidate + AudioVerification.ReceiptExtension)) return candidate;
            }
            throw new IOException("Unable to allocate a unique filename.");
        }
    }
}
