using System.Collections.Generic;
using System.IO;
using UnityEngine;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine.SceneManagement;

public static class LocalizationManager
{
    public static string NowLoc { get => _nowLoc; }

    public static string LocalizationPath = Path.Combine(SettingManager.DataDirectory, "Localization");
    private static string _nowLoc = SettingManager.Settings["language"];

    private static List<BaseUIController> allUIComponment = new List<BaseUIController>();

    private static JObject locTexts;

    static LocalizationManager()
    {
        SceneManager.activeSceneChanged += (_, __) => RefreshUI();
#if !UNITY_EDITOR
        // Also materialize bundled languages when only the player files were copied,
        // or when a user adds a missing language after the build was produced.
        try
        {
            Directory.CreateDirectory(LocalizationPath);
            foreach (var asset in Resources.LoadAll<TextAsset>("Localization"))
            {
                string target = Path.Combine(LocalizationPath, asset.name + ".json");
                if (!File.Exists(target)) File.WriteAllText(target, asset.text);
            }
        }
        catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException)
        {
            AppLog.Warning("Cannot create localization files: " + e.Message);
        }
#endif
    }

    public static void UpdateLoc(string nowLoc)
    {
        _nowLoc = nowLoc;

        string path = Path.Combine(LocalizationPath, $"{_nowLoc}.json");
        string json = null;
        try { if (File.Exists(path)) json = File.ReadAllText(path); }
        catch (System.Exception e) when (e is IOException || e is System.UnauthorizedAccessException) { AppLog.Warning(e.Message); }
        if (json == null)
        {
            var asset = Resources.Load<TextAsset>("Localization/" + _nowLoc) ?? Resources.Load<TextAsset>("Localization/en");
            json = asset != null ? asset.text : "{}";
        }
        try { locTexts = JObject.Parse(json); }
        catch (JsonException e)
        {
            locTexts = new JObject();
            AppLog.Warning("Cannot parse localization " + _nowLoc + ": " + e.Message);
        }

        RefreshUI();

        SettingManager.UpdateInfo("language", _nowLoc);
    }

    public static void AddComponment(BaseUIController c) => allUIComponment.Add(c);

    // Static callers resolve against the active scene. UI objects can supply their
    // own scene so cached/additively loaded pages do not use another page's text.
    public static string GetText(string target) => GetText(target, SceneManager.GetActiveScene().name);

    public static string GetText(string target, string sceneName)
    {
        if (locTexts == null) UpdateLoc(_nowLoc);
        if (string.IsNullOrEmpty(target)) return target;
        if (!string.IsNullOrEmpty(sceneName) && TryText(locTexts[sceneName] as JObject, target, out string text)) return text;
        if (TryText(locTexts["Common"] as JObject, target, out text)) return text;
        // Existing user-provided flat language files remain readable.
        return TryText(locTexts, target, out text) ? text : target;
    }

    static bool TryText(JObject section, string key, out string text)
    {
        JToken value = section?[key];
        text = value?.Type == JTokenType.String ? value.Value<string>() : null;
        return text != null;
    }

    static void RefreshUI()
    {
        if (locTexts == null) return;
        allUIComponment.RemoveAll(c => c == null);
        foreach (var component in allUIComponment.ToArray())
            if (component != null) component.UpdateLocalization();
    }
}
