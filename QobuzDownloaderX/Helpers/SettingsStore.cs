using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml;
using System.Xml.Linq;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class SettingsStore
    {
        private readonly string path, legacy;
        internal SettingsStore(string path, string legacy = null) { this.path = Path.GetFullPath(path); this.legacy = legacy; }
        internal Dictionary<string, string> Read()
        {
            using (var mutex = new Mutex(false, AtomicFiles.LockName(path)))
            {
                AtomicFiles.Acquire(mutex, CancellationToken.None);
                try { return ReadLocked(); }
                finally { mutex.ReleaseMutex(); }
            }
        }
        private Dictionary<string, string> ReadLocked()
        {
            if (!File.Exists(path))
            {
                if (File.Exists(path + ".bak")) return Load(path + ".bak");
                return legacy != null && File.Exists(legacy) ? Load(legacy) : new Dictionary<string, string>();
            }
            try { return Load(path); }
            catch (XmlException)
            {
                if (!File.Exists(path + ".bak")) throw new InvalidDataException("The settings file is damaged and has no recovery copy: " + path);
                var recovered = Load(path + ".bak");
                File.Copy(path, path + ".damaged-" + Guid.NewGuid().ToString("N"));
                return recovered;
            }
        }
        private static Dictionary<string, string> Load(string source)
        {
            using (var file = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete))
            using (var reader = XmlReader.Create(file, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, MaxCharactersInDocument = 4 * 1024 * 1024 }))
            {
                var doc = XDocument.Load(reader);
                if (doc.Root == null || doc.Root.Name != "settings") throw new XmlException("Unexpected settings document.");
                return doc.Root.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
            }
        }
        internal void Update(IDictionary<string, string> changes)
        {
            if (changes.Count == 0) return;
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            using (var mutex = new Mutex(false, AtomicFiles.LockName(path)))
            {
                AtomicFiles.Acquire(mutex, CancellationToken.None);
                string staged = Path.Combine(Path.GetDirectoryName(path), ".settings-" + Guid.NewGuid().ToString("N") + ".tmp");
                try
                {
                    var values = ReadLocked();
                    foreach (var change in changes) values[change.Key] = change.Value;
                    var doc = new XDocument(new XElement("settings", values.Select(v => new XElement(v.Key, v.Value))));
                    AtomicFiles.WriteText(staged, doc.ToString());
                    if (File.Exists(path))
                    {
                        // A corrupt primary must never replace the last good backup.
                        bool valid = true; try { Load(path); } catch (XmlException) { valid = false; }
                        File.Replace(staged, path, valid ? path + ".bak" : null);
                    }
                    else File.Move(staged, path);
                }
                finally { AtomicFiles.TryDelete(staged); mutex.ReleaseMutex(); }
            }
        }
    }
}
