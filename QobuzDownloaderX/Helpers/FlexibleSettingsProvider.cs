using System.Collections.Generic;
using System.Collections.Specialized;
using System.Configuration;
using System.IO;
using QobuzDownloaderX.Helpers;

public class FlexibleSettingsProvider : SettingsProvider
{
    public readonly string ConfigFileName = "user.config";
    public readonly string ConfigDirectoryPath;
    public readonly bool AppendVersion = false, AppendHash = false;
    private readonly SettingsStore store;
    public override string ApplicationName { get => "QobuzDownloaderX"; set { } }
    public string ConfigDirectoryPathFallback => AppPaths.DataDirectory;
    public string ConfigFileFullName => Path.Combine(ConfigDirectoryPath, ConfigFileName);
    public FlexibleSettingsProvider() : this(AppPaths.DataDirectory, Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "user.config")) { }
    internal FlexibleSettingsProvider(string directory, string legacy = null)
    {
        ConfigDirectoryPath = Path.GetFullPath(directory);
        store = new SettingsStore(ConfigFileFullName, legacy);
    }
    public override void Initialize(string name, NameValueCollection config) => base.Initialize(string.IsNullOrEmpty(name) ? nameof(FlexibleSettingsProvider) : name, config);
    public override SettingsPropertyValueCollection GetPropertyValues(SettingsContext context, SettingsPropertyCollection properties)
    {
        var stored = store.Read();
        var result = new SettingsPropertyValueCollection();
        foreach (SettingsProperty property in properties)
            result.Add(new SettingsPropertyValue(property) { SerializedValue = stored.TryGetValue(property.Name, out var value) ? value : property.DefaultValue, IsDirty = false });
        return result;
    }
    public override void SetPropertyValues(SettingsContext context, SettingsPropertyValueCollection values)
    {
        var changes = new Dictionary<string, string>();
        foreach (SettingsPropertyValue value in values)
            if (value.IsDirty) changes[value.Name] = value.SerializedValue?.ToString() ?? "";
        store.Update(changes);
        foreach (SettingsPropertyValue value in values) value.IsDirty = false;
    }
}
