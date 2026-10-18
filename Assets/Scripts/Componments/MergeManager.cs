using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SFB;
using UnityEngine;

public class MergeManager : MonoBehaviour
{
    public GameObject SelectBarPrefab;
    public Transform SelectBarContentParent;

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
        if (paths.Length == 0)
        {
            Debug.LogError("請先加入至少一個 .charamaker 檔案。");
            return;
        }
        string[] folders = StandaloneFileBrowser.OpenFolderPanel("選擇輸出 data 資料夾", "", false);
        if (folders == null || folders.Length == 0 || string.IsNullOrWhiteSpace(folders[0])) return;

        var errors = new List<string>();
        var warnings = new List<string>();
        var candidates = new List<(string Path, CharacterMakerDocument Document)>();
        foreach (string path in paths)
        {
            try { candidates.Add((path, CharacterMakerDocument.Read(path))); }
            catch (Exception exception) { errors.Add(Path.GetFileName(path) + "：" + exception.Message); }
        }

        var lastByTarget = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < candidates.Count; i++)
            if (!string.IsNullOrWhiteSpace(candidates[i].Document.targetId)) lastByTarget[candidates[i].Document.targetId] = i;
        var exporter = new CharacterMakerBatchExporter();
        var completed = new List<CharacterMakerDocument>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var candidate = candidates[i];
            string id = candidate.Document.targetId;
            if (!string.IsNullOrWhiteSpace(id) && lastByTarget.TryGetValue(id, out int last) && last != i)
            {
                warnings.Add(Path.GetFileName(candidate.Path) + "：同角色 " + id + " 已由後面的存檔覆蓋。");
                continue;
            }
            try
            {
                CharacterMakerBatchExporter.BuiltCharacter built = exporter.Build(candidate.Document);
                CharacterMakerBatchExporter.Write(built, folders[0]);
                completed.Add(candidate.Document);
                if (built.Portrait == null)
                    warnings.Add(Path.GetFileName(candidate.Path) + "：沒有內嵌頭像，已略過頭像輸出。");
            }
            catch (Exception exception)
            {
                errors.Add(Path.GetFileName(candidate.Path) + "：" + exception.Message);
            }
        }

        if (completed.Count > 0)
        {
            try { CharacterMakerBatchExporter.WriteMergedCharacterSettings(completed, folders[0]); }
            catch (Exception exception) { errors.Add("合併角色設定：" + exception.Message); }
        }
        Debug.Log("批次匯出完成：成功 " + completed.Count + "，失敗 " + errors.Count + "，警告 " + warnings.Count + "。\n" + folders[0]);
        if (warnings.Count > 0) Debug.LogWarning(string.Join("\n", warnings));
        if (errors.Count > 0) Debug.LogError(string.Join("\n", errors));
    }
}
