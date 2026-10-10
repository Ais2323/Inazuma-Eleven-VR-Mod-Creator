using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;

public static class LocalizationManager
{
    public static string NowLoc { get => _nowLoc; }

#if UNITY_EDITOR
    public static string LocalizationPath = Path.Combine(Application.dataPath, "Localization");
#else
    public static string LocalizationPath = Path.Combine(Application.persistentDataPath, "Localization");
#endif
    private static string _nowLoc = SettingManager.Settings["language"];

    private static List<BaseUIController> allUIComponment = new List<BaseUIController>();

    private static Dictionary<string, string> locTexts = null;

    public static void UpdateLoc(string nowLoc)
    {
        // 更新語系
        _nowLoc = nowLoc;

        // 更新檔案
        string path = Path.Combine(LocalizationPath, $"{_nowLoc}.json");
        string json = null;
        try { if (File.Exists(path)) json = File.ReadAllText(path); }
        catch (IOException e) { Debug.LogWarning(e.Message); }
        if (json == null)
        {
            var asset = Resources.Load<TextAsset>("Localization/" + _nowLoc) ?? Resources.Load<TextAsset>("Localization/en");
            json = asset != null ? asset.text : "{}";
        }
        try { locTexts = JsonConvert.DeserializeObject<Dictionary<string, string>>(json) ?? new Dictionary<string, string>(); }
        catch (JsonException) { locTexts = new Dictionary<string, string>(); }

        // 更新所有UI
        allUIComponment.RemoveAll(c => c == null);
        allUIComponment.ForEach(c => c.UpdateLocalization());

        SettingManager.UpdateInfo("language", _nowLoc);
    }

    // 加入UI
    public static void AddComponment(BaseUIController c) => allUIComponment.Add(c);

    public static string GetText(string target)
    {
        if (locTexts == null) UpdateLoc(_nowLoc);
        return locTexts.TryGetValue(target, out var text) ? text : target;
    }
}
