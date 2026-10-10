using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using UnityEngine;

public static class SettingManager
{
    #if UNITY_EDITOR
        public static string SettingPath = Path.Combine(Application.dataPath, "setting.json");
    #else
        public static string SettingPath = Path.Combine(Application.persistentDataPath, "setting.json");
    #endif

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
