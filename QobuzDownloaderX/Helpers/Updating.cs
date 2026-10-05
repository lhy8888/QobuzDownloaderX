using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ZetaLongPaths;

namespace QobuzDownloaderX.Helpers
{
    internal sealed class TranslationUpdater
    {
        internal static readonly TimeSpan updateCheckTimeout = TimeSpan.FromSeconds(30);

        static readonly Regex removeTimezoneRegEx = new Regex(@"[A-Z]{2,5}(\+?\d{0,2})?$", RegexOptions.Compiled);

        // List of language files
        private static readonly Dictionary<string, string> LanguageFiles = new Dictionary<string, string>
        {
            { "de.json", "Languages/de.json" },
            { "en.json", "Languages/en.json" },
            { "es.json", "Languages/es.json" },
            { "fr.json", "Languages/fr.json" },
            { "ru.json", "Languages/ru.json" },
            { "tr.json", "Languages/tr.json" },
            { "zh-cn.json", "Languages/zh-cn.json" }
        };

        private static void SaveLanguage(string path, string content)
        {
            JObject.Parse(content);
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string staged = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try { AtomicFiles.WriteText(staged, content); if (File.Exists(path)) File.Replace(staged, path, null); else File.Move(staged, path); }
            finally { AtomicFiles.TryDelete(staged); }
        }

        public static string NormalizeDate(string dateStr)
        {
            // Remove any alphabetic timezone part
            return removeTimezoneRegEx.Replace(dateStr ?? "", "").Trim();
        }

        public static async Task CheckAndUpdateLanguageFiles()
        {
            try
            {
                using (HttpClient client = new HttpClient() { Timeout = updateCheckTimeout })
                {
                    client.DefaultRequestHeaders.Add("User-Agent", "TranslationUpdater");

                    foreach (var languageFile in LanguageFiles)
                    {
                        string fileName = languageFile.Key;
                        string localFilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "languages", fileName);

                        string apiUrl = $"https://api.github.com/repos/lhy8888/QobuzDownloaderX/contents/QobuzDownloaderX/Resources/{languageFile.Value}";

                        try
                        {
                            using (HttpResponseMessage response = await client.GetAsync(apiUrl))
                            {
                                if (response.IsSuccessStatusCode)
                                {
                                    string jsonResponse = await response.Content.ReadAsStringAsync();
                                    JObject fileMetadata = JObject.Parse(jsonResponse);

                                    // Retrieve the download URL for the remote file
                                    string downloadUrl = fileMetadata["download_url"]?.ToString();
                                    if (!string.IsNullOrEmpty(downloadUrl))
                                    {
                                        // Fetch the remote file content
                                        string remoteContent = await client.GetStringAsync(downloadUrl);

                                        // Parse the "TranslationUpdatedOn" field from the remote file
                                        JObject remoteJson = JObject.Parse(remoteContent);
                                        string remoteUpdatedOnString = remoteJson["TranslationUpdatedOn"]?.ToString();

                                        // Parse the local file's "TranslationUpdatedOn" field
                                        if (ZlpIOHelper.FileExists(localFilePath))
                                        {
                                            string localContent = ZlpIOHelper.ReadAllText(localFilePath);
                                            JObject localJson = JObject.Parse(localContent);
                                            string localUpdatedOnString = localJson["TranslationUpdatedOn"]?.ToString();

                                            // Compare updated date
                                            if (DateTime.TryParse(NormalizeDate(remoteUpdatedOnString), out DateTime remoteDate) &&
                                                DateTime.TryParse(NormalizeDate(localUpdatedOnString), out DateTime localDate))
                                            {
                                                if (remoteDate > localDate)
                                                {
                                                    SaveLanguage(localFilePath, remoteContent);
                                                    qbdlxForm._qbdlxForm.logger.Debug($"File {fileName} updated successfully.");
                                                }
                                                else
                                                {
                                                    qbdlxForm._qbdlxForm.logger.Debug($"File {fileName} is already up-to-date.");
                                                }
                                            }
                                            else
                                            {
                                                qbdlxForm._qbdlxForm.logger.Error($"Failed to parse dates for {fileName}. Remote: '{remoteUpdatedOnString}', Local: '{localUpdatedOnString}'");
                                            }
                                        }
                                        else
                                        {
                                            // Local file does not exist, download it
                                            SaveLanguage(localFilePath, remoteContent);
                                            qbdlxForm._qbdlxForm.logger.Debug($"File {fileName} downloaded successfully.");
                                        }
                                    }
                                    else
                                    {
                                        qbdlxForm._qbdlxForm.logger.Error($"Failed to retrieve the download URL for {fileName}.");
                                    }
                                }
                                else
                                {
                                    qbdlxForm._qbdlxForm.logger.Error($"Failed to fetch metadata for {fileName}: {response.StatusCode}");
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            qbdlxForm._qbdlxForm.logger.Error($"Error updating {fileName}: {ex.Message}");
                        }
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                qbdlxForm._qbdlxForm.logger.Error($"Error. CheckAndUpdateLanguageFiles has timed out: {ex.Message}");
            }
            catch (Exception ex)
            {
                qbdlxForm._qbdlxForm.logger.Error($"Error updating: {ex.Message}");
            }
        }
    }

    internal static class VersionChecker
    {
        public static async Task<(bool isUpdateAvailable, string newVersion, string currentVersion, string changes)> CheckForUpdate()
        {
            string changes = string.Empty;
            string currentVersion = string.Empty;
            string newVersion = string.Empty;
            bool isUpdateAvailable = false;

            try
            {
                // Initialize HttpClient to fetch version number from GitHub
                using (var httpClient = new HttpClient() { Timeout = TranslationUpdater.updateCheckTimeout })
                {
                    qbdlxForm._qbdlxForm.logger.Debug("HttpClient initialized");

                    // Set user-agent to Firefox
                    httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:67.0) Gecko/20100101 Firefox/67.0");

                    // Request the latest release from GitHub
                    qbdlxForm._qbdlxForm.logger.Debug("Requesting latest GitHub release");
                    var versionUrl = "https://api.github.com/repos/lhy8888/QobuzDownloaderX/releases/latest";
                    using (var response = await httpClient.GetAsync(versionUrl))
                    {
                    response.EnsureSuccessStatusCode();
                    string responseString = await response.Content.ReadAsStringAsync();

                    // Parse the JSON response
                    JObject json = JObject.Parse(responseString);

                    // Extract version number and changelog
                    newVersion = (string)json["tag_name"];
                    changes = (string)json["body"];
                    qbdlxForm._qbdlxForm.logger.Debug("Received version from GitHub: " + newVersion);

                    // Get the current version from the assembly
                    currentVersion = Assembly.GetExecutingAssembly().GetName().Version.ToString();

                    // Compare versions numerically
                    var newVersionObj = new Version(newVersion?.TrimStart('v')); // Remove leading 'v' if present
                    var currentVersionObj = new Version(currentVersion);

                    isUpdateAvailable = newVersionObj > currentVersionObj;

                    if (isUpdateAvailable)
                    {
                        qbdlxForm._qbdlxForm.logger.Debug("New version available: " + newVersion);
                    }
                    else
                    {
                        qbdlxForm._qbdlxForm.logger.Debug("Current version matches the latest release.");
                    }
                    }
                }
            }
            catch (OperationCanceledException ex)
            {
                qbdlxForm._qbdlxForm.logger.Error($"Error. VersionChecker has timed out: {ex.Message}");
            }
            catch (Exception ex)
            {
                qbdlxForm._qbdlxForm.logger.Error("Failed to connect to GitHub: " + ex.Message);
            }

            return (isUpdateAvailable, newVersion, currentVersion, changes);
        }
    }

}
