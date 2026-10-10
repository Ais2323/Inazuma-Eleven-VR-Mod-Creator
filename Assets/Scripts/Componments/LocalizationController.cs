using System.Collections.Generic;
using System.IO;
using TMPro;
using UnityEngine;

public class LocalizationController : MonoBehaviour
{
    public TMP_Dropdown dropdown;
    void Awake()
    {
        string path = LocalizationManager.LocalizationPath;
        string[] allLocName = Directory.Exists(path) ? Directory.GetFiles(path, "*.json") : new string[0];
        if (allLocName.Length == 0)
            allLocName = System.Array.ConvertAll(Resources.LoadAll<TextAsset>("Localization"), asset => asset.name + ".json");
        List<TMP_Dropdown.OptionData> options = new();
        int optionIdx = 0;
        for (int i = 0; i < allLocName.Length; i++)
        {
            string filePath = allLocName[i];
            string fileName = Path.GetFileNameWithoutExtension(filePath);
            TMP_Dropdown.OptionData option = new(fileName);
            options.Add(option);

            if (fileName == LocalizationManager.NowLoc)
                optionIdx = i;
        }
        dropdown.ClearOptions();
        dropdown.AddOptions(options);
        if (options.Count == 0) { LocalizationManager.UpdateLoc("en"); return; }

        dropdown.value = optionIdx;
        LocalizationManager.UpdateLoc(dropdown.options[optionIdx].text);
    }

    public void UpdateLocText(int index)
    {
        int nowIdx = dropdown.value;
        string nowLoc = dropdown.options[nowIdx].text;
        LocalizationManager.UpdateLoc(nowLoc);
    }
}
