using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SFB;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;

public class MergeManager : MonoBehaviour
{
    public GameObject SelectBarPrefab;
    public Transform SelectBarContentParent;
    public InputField modNameInput;

    public void OnClickOpen()
    {
        string[] paths = StandaloneFileBrowser.OpenFilePanel(
            "選擇 CharacterMaker 存檔", "", "charamaker", true);
        if (paths == null || paths.Length == 0) return;
        var existing = new HashSet<string>(
            SelectBarContentParent.GetComponentsInChildren<SelectBarController>(true)
                .Where(value => !string.IsNullOrWhiteSpace(value.path))
                .Select(value => Path.GetFullPath(value.path)),
            StringComparer.OrdinalIgnoreCase);
        foreach (string selected in paths)
        {
            if (string.IsNullOrWhiteSpace(selected)) continue;
            string path;
            try { path = Path.GetFullPath(selected); }
            catch (Exception exception) { Debug.LogWarning("無效路徑：" + exception.Message); continue; }
            if (!existing.Add(path)) continue;
            GameObject bar = Instantiate(SelectBarPrefab, SelectBarContentParent, false);
            SelectBarController controller = bar.GetComponent<SelectBarController>();
            if (controller == null)
            {
                Destroy(bar);
                Debug.LogError("SelectBarPrefab 缺少 SelectBarController。");
                continue;
            }
            controller.SetPath(path);
        }
    }

    public void OnClickExport()
    {
        string[] paths = SelectBarContentParent.GetComponentsInChildren<SelectBarController>(true)
            .Select(value => value.path).Where(value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (paths.Length == 0) { Debug.LogError("請先加入 .charamaker 存檔。"); return; }
        string modName = modNameInput != null ? modNameInput.text.Trim() : "";
        if (string.IsNullOrWhiteSpace(modName)) { Debug.LogError("請先輸入模組名稱。"); return; }
        string[] folders = StandaloneFileBrowser.OpenFolderPanel("選擇模組輸出位置", SettingManager.GetExportDirectory("modExportDirectory"), false);
        if (folders == null || folders.Length == 0 || string.IsNullOrWhiteSpace(folders[0])) return;
        SettingManager.RememberExportDirectory("modExportDirectory", folders[0]);
        try {
            var documents = new List<CharacterMakerDocument>();
            foreach (string path in paths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)) {
                try { documents.Add(CharacterMakerDocument.Read(path)); }
                catch (Exception e) { throw new InvalidDataException(Path.GetFileName(path) + "：" + e.Message, e); }
            }
            string modRoot = ExportMod(modName, folders[0], documents);
            Debug.Log("模組匯出完成：" + modRoot + "\n每次匯出都是獨立版本，請只啟用同一批角色的一份模組。");
        } catch (Exception e) { Debug.LogError("合併匯出失敗：" + e.Message); }
    }
    public static string ExportMod(string modName, string parent, IEnumerable<CharacterMakerDocument> documents)
    {
        modName = (modName ?? "").Trim();
        if (string.IsNullOrWhiteSpace(modName)) throw new InvalidDataException("請輸入模組名稱。");
        string prefix = new string(modName.Select(c => c < 32 || "<>:\"/\\|?*".Contains(c) || char.IsWhiteSpace(c) ? '_' : c).ToArray()).Trim(' ', '.', '_');
        if (string.IsNullOrWhiteSpace(prefix)) throw new InvalidDataException("模組名稱沒有可用的資料夾字元。");
        string directory = Path.GetFullPath(parent);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        string target = Path.Combine(directory, prefix);
        if (Directory.Exists(target) || File.Exists(target))
            throw new InvalidDataException("輸出位置已有同名模組：" + target + "。請選擇其他輸出位置；不會自動附加編號或覆蓋原檔。");
        string stage = Path.Combine(directory, ".cmp-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        if (Path.GetDirectoryName(Path.GetFullPath(stage)) != directory || Path.GetDirectoryName(Path.GetFullPath(target)) != directory)
            throw new InvalidDataException("Invalid mod output path.");
        Directory.CreateDirectory(stage);
        try {
            var ids = CharacterMakerExportBatch.Export(documents, Path.Combine(stage, "data"), modName);
            var metadata = new JObject { ["Name"] = modName, ["Author"] = "", ["ModVersion"] = "1.0", ["GameVersion"] = "", ["ModLink"] = "" };
            File.WriteAllText(Path.Combine(stage, "mod_data.json"), metadata.ToString());
            // A newly owned staging folder is moved into a checked sibling, never an existing mod.
            Directory.Move(stage, target);
            return target;
        } finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

}
