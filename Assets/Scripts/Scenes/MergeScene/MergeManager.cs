using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SFB;
using UnityEngine;
using UnityEngine.UI;
using Newtonsoft.Json.Linq;
using System.Text;

public enum ModExportType { Viola, InazumaFantasy }

public class MergeManager : MonoBehaviour
{
    public GameObject SelectBarPrefab;
    public Transform SelectBarContentParent;
    public InputField modNameInput;
    public Dropdown exportTypeDropdown;

    public void OnClickOpen()
    {
        string[] paths = StandaloneFileBrowser.OpenFilePanel(
            "Select CharacterMaker / UniformMaker saves", "", new[] { new ExtensionFilter("Maker saves", "charamaker", "uniformmaker") }, true);
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
        if (paths.Length == 0) { Debug.LogError("Add .charamaker or .uniformmaker saves first."); return; }
        string modName = modNameInput != null ? modNameInput.text.Trim() : "";
        if (string.IsNullOrWhiteSpace(modName)) { Debug.LogError("請先輸入模組名稱。"); return; }
        string[] folders = StandaloneFileBrowser.OpenFolderPanel("選擇模組輸出位置", SettingManager.GetExportDirectory("modExportDirectory"), false);
        if (folders == null || folders.Length == 0 || string.IsNullOrWhiteSpace(folders[0])) return;
        SettingManager.RememberExportDirectory("modExportDirectory", folders[0]);
        try {
            var documents = new List<CharacterMakerDocument>();
            var uniforms = new List<UniformMakerDocument>();
            foreach (string path in paths.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.OrdinalIgnoreCase)) {
                try {
                    if (Path.GetExtension(path).Equals(".uniformmaker", StringComparison.OrdinalIgnoreCase)) uniforms.Add(UniformMakerDocument.Read(path));
                    else if (Path.GetExtension(path).Equals(".charamaker", StringComparison.OrdinalIgnoreCase)) documents.Add(CharacterMakerDocument.Read(path));
                    else throw new InvalidDataException("Unsupported Maker save: " + path);
                }
                catch (Exception e) { throw new InvalidDataException(Path.GetFileName(path) + "：" + e.Message, e); }
            }
            var exportType = exportTypeDropdown != null ? (ModExportType)exportTypeDropdown.value : ModExportType.Viola;
            string modRoot = ExportMod(modName, folders[0], documents, uniforms, exportType);
            Debug.Log("模組匯出完成：" + modRoot + "\n每次匯出都是獨立版本，請只啟用同一批角色的一份模組。");
        } catch (Exception e) { Debug.LogError("合併匯出失敗：" + e.Message); }
    }
    public static string ExportMod(string modName, string parent, IEnumerable<CharacterMakerDocument> documents, IEnumerable<UniformMakerDocument> uniforms = null,
        ModExportType exportType = ModExportType.Viola)
    {
        if (!Enum.IsDefined(typeof(ModExportType), exportType)) throw new ArgumentOutOfRangeException(nameof(exportType));
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
            var characters = documents?.ToArray() ?? Array.Empty<CharacterMakerDocument>();
            var kits = uniforms?.ToArray() ?? Array.Empty<UniformMakerDocument>();
            if (characters.Length == 0 && kits.Length == 0) throw new InvalidDataException("No Maker documents selected.");
            string data = Path.Combine(stage, "data");
            Directory.CreateDirectory(data);
            bool viola = exportType == ModExportType.Viola;
            if (characters.Length > 0) CharacterMakerExportBatch.Export(characters, data, modName, writeRegistry: viola);
            if (kits.Length > 0) UniformMakerExporter.Export(kits, data, modName, writeRegistry: viola);
            if (viola) {
                var metadata = new JObject { ["Name"] = modName, ["Author"] = "", ["ModVersion"] = "1.0", ["GameVersion"] = "", ["ModLink"] = "" };
                File.WriteAllText(Path.Combine(stage, "mod_data.json"), metadata.ToString());
            }
            else {
                UniformMakerExporter.WriteFantasyShop(kits, data, stage, modName);
                if (kits.Length > 0 && characters.Length == 0)
                    UniformMakerExporter.WriteFantasyUniformResources(data, stage, modName);
                WriteFantasyPackage(stage, data, prefix, modName);
            }
            // A newly owned staging folder is moved into a checked sibling, never an existing mod.
            Directory.Move(stage, target);
            return target;
        } finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    // The Fantasy ModLoader reads assets by their game-relative path (without
    // data/common or data/dx11). It infers the root from the native file type and
    // game catalog. In particular, cpk_list.cfg.bin must never be overridden.
    static void WriteFantasyPackage(string stage, string data, string id, string name)
    {
        if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(data)), Path.GetFullPath(stage), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Fantasy data must belong to the current staging directory.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string root in new[] { "common", "dx11" }) {
            string source = Path.Combine(data, root);
            if (!Directory.Exists(source)) continue;
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)) {
                string relative = file.Substring(source.Length + 1);
                if (files.TryGetValue(relative, out var previous))
                    throw new InvalidDataException("Inazuma Fantasy asset path occurs in both common and dx11: " + relative + " (" + previous + ")");
                files.Add(relative, file);
            }
        }
        foreach (var file in files) {
            string target = Path.Combine(stage, "assets", file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.Move(file.Value, target);
        }
        // Only this export's newly created staging directory is removed.
        Directory.Delete(data, true);
        string Quote(string value) => Newtonsoft.Json.JsonConvert.ToString(value);
        string manifest = "id = " + Quote(id) + "\nname = " + Quote(name) + "\nversion = \"1.0.0\"\nauthor = \"\"\n" +
            "description = \"Created with Inazuma Eleven VR Mod Creator\"\npriority = 0\nloader_min = \"2.0.0\"\n" +
            "prefix = " + Quote(id) + "\nfolder = " + Quote(id) + "\n";
        if (File.Exists(Path.Combine(stage, "content", "shops", "uniforms.toml")))
            manifest += "requires = [\"VRFW>=0.2.4\"]\noptional = [\"InazumaFantasy\"]\n";
        File.WriteAllText(Path.Combine(stage, "mod.toml"), manifest, new UTF8Encoding(false));
    }

}
