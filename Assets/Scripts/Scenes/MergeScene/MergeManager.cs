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

    public void OnClickOpen() => Loading.Perform(() => OnClickOpenCore(), "LoadingRead");

    void OnClickOpenCore()
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
            catch (Exception exception) { AppLog.Warning("無效路徑：" + exception.Message); continue; }
            if (!existing.Add(path)) continue;
            GameObject bar = Instantiate(SelectBarPrefab, SelectBarContentParent, false);
            SelectBarController controller = bar.GetComponent<SelectBarController>();
            if (controller == null)
            {
                Destroy(bar);
                AppLog.Error("SelectBarPrefab 缺少 SelectBarController。");
                continue;
            }
            controller.SetPath(path);
        }
    }

    public void OnClickExport() => StartCoroutine(Loading.Track(ExportSelected(), "LoadingExport"));

    System.Collections.IEnumerator ExportSelected()
    {
        string[] paths = SelectBarContentParent.GetComponentsInChildren<SelectBarController>(true)
            .Select(value => value.path).Where(value => !string.IsNullOrWhiteSpace(value))
            .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ThenBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        if (paths.Length == 0) { AppLog.Error("Add .charamaker or .uniformmaker saves first."); yield break; }
        string modName = modNameInput != null ? modNameInput.text.Trim() : "";
        if (string.IsNullOrWhiteSpace(modName)) { AppLog.Error("Enter a Mod name first."); yield break; }
        string[] folders = StandaloneFileBrowser.OpenFolderPanel("Export Mod", SettingManager.GetExportDirectory("modExportDirectory"), false);
        if (folders == null || folders.Length == 0 || string.IsNullOrWhiteSpace(folders[0])) yield break;
        SettingManager.RememberExportDirectory("modExportDirectory", folders[0]);
        var exportType = exportTypeDropdown != null ? (ModExportType)exportTypeDropdown.value : ModExportType.Viola;
        var documents = new List<CharacterMakerDocument>();
        var uniforms = new List<UniformMakerDocument>();
        for (int i = 0; i < paths.Length; i++) {
            string path = paths[i];
            Loading.UpdateText(LocalizationManager.GetText("LoadingRead") + " " + Path.GetFileName(path));
            Loading.UpdateProgress(i * 30 / paths.Length);
            yield return null;
            bool failed = false;
            try {
                if (Path.GetExtension(path).Equals(".uniformmaker", StringComparison.OrdinalIgnoreCase)) uniforms.Add(UniformMakerDocument.Read(path));
                else if (Path.GetExtension(path).Equals(".charamaker", StringComparison.OrdinalIgnoreCase)) documents.Add(CharacterMakerDocument.Read(path));
                else throw new InvalidDataException("Unsupported Maker save: " + path);
            } catch (Exception error) { AppLog.Error(Path.GetFileName(path) + ": " + error.Message); failed = true; }
            if (failed) yield break;
        }
        Loading.UpdateText(LocalizationManager.GetText("LoadingExport") + " " + modName);
        Loading.UpdateProgress(35);
        yield return null;
        try {
            string modRoot = ExportMod(modName, folders[0], documents, uniforms, exportType);
            AppLog.Result("Mod exported: " + modRoot);
        } catch (Exception error) { AppLog.Error("Mod export failed: " + error.Message); }
        Loading.UpdateProgress(100);
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
