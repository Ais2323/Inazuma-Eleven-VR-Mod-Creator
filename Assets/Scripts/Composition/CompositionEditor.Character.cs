using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

namespace G4Composition
{
    public sealed partial class CompositionEditor
    {
        CharacterCatalog catalog;
        NewCharacterSettings characterSettings;
        GameObject characterPanel;
        bool newCharacter;
        Text exportMode;
        async void ConfigureCharacter()
        {
            try
            {
                if (catalog == null)
                {
                    Status("讀取普通角色、能力變體與招式…");
                    catalog = await Task.Run(() => CharacterCatalog.Read(DumpDataManager.CommonPath, lifetime.Token), lifetime.Token);
                    lifetime.Token.ThrowIfCancellationRequested();
                }
                if (characterPanel != null) { characterPanel.SetActive(true); return; }
                var backdrop = Panel("Character setup", canvas.transform, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
                characterPanel = backdrop.gameObject; backdrop.GetComponent<Image>().color = new Color(.025f, .035f, .06f, .99f);
                Label(backdrop, "新增普通角色：範本與能力設定", 24, 220, 20, 1000, 40);
                Label(backdrop, "身體、聲音與成長曲線沿用範本；下列七項是角色修正值。", 16, 220, 64, 1000, 30);
                var body = Scroll(backdrop, 220, 108, 1000, 674); body.sizeDelta = new Vector2(0, 800);
                var availableHeads = new HashSet<string>(heads.Select(h => h.RelativePath), StringComparer.OrdinalIgnoreCase);
                var candidates = catalog.Templates.Where(r => { try { return availableHeads.Contains(catalog.Face(r)); } catch { return false; } }).OrderBy(r => r.Text(1), StringComparer.Ordinal).ToArray();
                var baseDropdown = DropdownCopy(sourceDropdown, body, 0, 0, 470, 34); baseDropdown.ClearOptions(); baseDropdown.AddOptions(candidates.Select(r => r.Text(1) + " · " + r.UInt(0)).ToList());
                var parameterDropdown = DropdownCopy(sourceDropdown, body, 490, 0, 470, 34);
                var name = Input(body, "", 160, 48, 800, 32, value => characterSettings.DisplayName = value);
                Label(body, "顯示名稱", 16, 0, 48, 150, 32);
                var affinity = DropdownCopy(sourceDropdown, body, 160, 92, 220, 32); affinity.ClearOptions(); affinity.AddOptions(new List<string> { "無", "風", "林", "火", "山" });
                var main = DropdownCopy(sourceDropdown, body, 460, 92, 220, 32); main.ClearOptions(); main.AddOptions(new List<string> { "無", "守門 GK", "前鋒 FW", "中場 MF", "後衛 DF" });
                var sub = DropdownCopy(main, body, 740, 92, 220, 32);
                Label(body, "屬性", 16, 0, 92, 150, 32); Label(body, "主位置", 16, 385, 92, 75, 32); Label(body, "副位置", 16, 685, 92, 75, 32);
                affinity.onValueChanged.AddListener(value => characterSettings.Affinity = value);
                main.onValueChanged.AddListener(value => characterSettings.MainPosition = value); sub.onValueChanged.AddListener(value => characterSettings.SubPosition = value);
                string[] labels = { "射門", "控球", "技術", "壓迫", "身體", "敏捷", "智力" };
                var stats = new List<InputField>();
                for (int i = 0; i < 7; i++)
                {
                    int slot = i; float x = i % 4 * 240, y = 142 + i / 4 * 44;
                    Label(body, labels[i], 16, x, y, 70, 30);
                    stats.Add(Input(body, "0", x + 74, y, 150, 30, value => { if (int.TryParse(value, out int n)) characterSettings.StatModifiers[slot] = n; else Status("修正值必須是整數。"); }));
                }
                uint[] skillIds = new[] { 0u }.Concat(catalog.Skills.Keys.OrderBy(id => id)).ToArray();
                var skills = new List<Dropdown>(); var levels = new List<InputField>();
                Label(body, "九個招式槽（選項來自目前 Dump）", 18, 0, 238, 960, 30);
                for (int i = 0; i < 9; i++)
                {
                    int slot = i; float y = 276 + i * 45;
                    Label(body, "招式 " + (i + 1), 16, 0, y, 90, 32);
                    var skill = DropdownCopy(sourceDropdown, body, 100, y, 650, 32); skill.ClearOptions(); skill.AddOptions(skillIds.Select(id => id == 0 ? "無" : catalog.SkillNames.TryGetValue(id, out string label) ? label + " · " + id : id.ToString() + " · 名稱 ID " + catalog.Skills[id]).ToList());
                    skill.onValueChanged.AddListener(value => characterSettings.Skills[slot] = skillIds[value]); skills.Add(skill);
                    Label(body, "等級", 16, 764, y, 70, 32);
                    levels.Add(Input(body, "1", 834, y, 125, 32, value => { if (int.TryParse(value, out int n) && n >= 0 && n <= 99) characterSettings.UnlockLevels[slot] = n; else Status("解鎖等級需為 0–99。"); }));
                }
                Action<CfgTable.Row> fill = parameter =>
                {
                    characterSettings.TemplateParameterId = parameter.UInt(0);
                    affinity.SetValueWithoutNotify((int)parameter.Int(2)); main.SetValueWithoutNotify((int)parameter.Int(3)); sub.SetValueWithoutNotify((int)parameter.Int(4));
                    characterSettings.Affinity = affinity.value; characterSettings.MainPosition = main.value; characterSettings.SubPosition = sub.value;
                    for (int i = 0; i < 7; i++) { characterSettings.StatModifiers[i] = checked((int)parameter.Int(29 + i)); stats[i].text = characterSettings.StatModifiers[i].ToString(); }
                    for (int i = 0; i < 9; i++)
                    {
                        uint id = parameter.UInt(11 + i * 2); int option = Array.IndexOf(skillIds, id);
                        // A passive/unknown skill is retained and will block export until the user chooses a verified skill.
                        characterSettings.Skills[i] = id; skills[i].SetValueWithoutNotify(Math.Max(0, option));
                        characterSettings.UnlockLevels[i] = checked((int)parameter.Int(12 + i * 2)); levels[i].text = characterSettings.UnlockLevels[i].ToString();
                    }
                };
                Action<int> choose = index =>
                {
                    try
                    {
                        var source = candidates[index]; string face = catalog.Face(source); int targetIndex = heads.ToList().FindIndex(h => string.Equals(h.RelativePath, face, StringComparison.OrdinalIgnoreCase));
                        if (targetIndex < 0) throw new InvalidOperationException("範本頭部不在可用清單：" + face);
                        characterSettings = new NewCharacterSettings { TemplateBaseId = source.UInt(0), DisplayName = source.Text(1) + " Custom" }; name.text = characterSettings.DisplayName;
                        var parameters = catalog.Parameters.Rows.Where(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(1) == source.UInt(0)).ToArray();
                        parameterDropdown.onValueChanged.RemoveAllListeners(); parameterDropdown.ClearOptions(); parameterDropdown.AddOptions(parameters.Select(p => "能力 " + p.UInt(0) + " / rarity " + p.Int(41)).ToList());
                        parameterDropdown.onValueChanged.AddListener(value => fill(parameters[value])); parameterDropdown.SetValueWithoutNotify(0); fill(parameters[0]);
                        targetDropdown.SetValueWithoutNotify(targetIndex); LoadTarget(targetIndex);
                    }
                    catch (Exception e) { Status(e.Message); }
                };
                baseDropdown.onValueChanged.AddListener(index => choose(index));
                int initial = Array.FindIndex(candidates, c => { try { return string.Equals(catalog.Face(c), target?.RelativePath, StringComparison.OrdinalIgnoreCase); } catch { return false; } });
                if (candidates.Length == 0) throw new InvalidOperationException("沒有可用的普通角色範本。");
                baseDropdown.SetValueWithoutNotify(Math.Max(initial, 0)); choose(Math.Max(initial, 0));
                Button(backdrop, "套用新增角色設定", 220, 800, 310, 40, () => { if (characterSettings == null) return; newCharacter = true; exportMode.text = "模式：新增普通角色"; characterPanel.SetActive(false); Status("新增角色模式：匯出包含獨立 ID、頭像、資料表副本與免費商店項目。"); });
                Button(backdrop, "返回拼貼", 550, 800, 220, 40, () => characterPanel.SetActive(false));
                Status("");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Status("角色設定：" + e.Message); Debug.LogException(e); }
        }
    }
}
