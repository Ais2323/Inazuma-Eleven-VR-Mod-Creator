using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

public static class SettingManager
{
    public static string DataDirectory
    {
        get
        {
#if UNITY_EDITOR
            return Application.dataPath;
#elif UNITY_STANDALONE_OSX
            return Path.GetDirectoryName(Path.GetDirectoryName(Application.dataPath));
#else
            return Path.GetDirectoryName(Application.dataPath) ?? Application.dataPath;
#endif
        }
    }
    public static string SettingPath = Path.Combine(DataDirectory, "setting.json");

    public static Dictionary<string, string> Settings {
        get
        {
            if (_setting == null) Init();
            return _setting;
        }
    }
    private static Dictionary<string, string> _setting = null;

    private static void Init()
    {
        _setting = new Dictionary<string, string>();
        bool createPortableSettings = !File.Exists(SettingPath);
        try
        {
            if (File.Exists(SettingPath))
                _setting = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(SettingPath)) ?? _setting;
        }
        catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException || e is JsonException)
        {
            Debug.LogWarning("Cannot read settings: " + e.Message);
        }
        if (!_setting.TryGetValue("path", out var path) || path == null) _setting["path"] = "";
        if (!_setting.TryGetValue("language", out var language) || string.IsNullOrWhiteSpace(language)) _setting["language"] = "en";
        if (createPortableSettings) SaveSettings();
    }

    // Export dialogs have their own persisted directories, independent of file-open history.
    public static string GetExportDirectory(string key)
    {
        if (Settings.TryGetValue(key, out string directory) && !string.IsNullOrWhiteSpace(directory))
        {
            try
            {
                directory = Path.GetFullPath(directory);
                while (!string.IsNullOrEmpty(directory))
                {
                    if (Directory.Exists(directory)) return directory;
                    directory = Path.GetDirectoryName(directory);
                }
            }
            catch (System.Exception e) when (e is System.ArgumentException || e is System.NotSupportedException || e is IOException || e is System.UnauthorizedAccessException) { }
        }
        string fallback = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
        return Directory.Exists(fallback) ? fallback : DataDirectory;
    }

    public static void RememberExportDirectory(string key, string directory)
    {
        if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            UpdateInfo(key, Path.GetFullPath(directory));
    }

    public static void UpdateInfo(string type, string value)
    {
        if (_setting == null) Init();
        if (_setting.ContainsKey(type))
        {
            if (_setting[type] == value) return;
            _setting[type] = value;
        }
        else _setting.Add(type, value);
        
        SaveSettings();
    }

    private static void SaveSettings()
    {
        string json = JsonConvert.SerializeObject(_setting);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingPath));
            File.WriteAllText(SettingPath, json);
        }
        catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException)
        {
            Debug.LogWarning("Cannot save settings: " + e.Message);
        }
    }
}
