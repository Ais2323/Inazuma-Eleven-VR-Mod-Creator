using System;
using System.Collections.Generic;
using System.Linq;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

[Serializable]
public sealed class VoiceSkillPage
{
    public int page;
    public Transform content;
    public GameObject template;
    public Button add;
}

public partial class CharacterVoiceController
{
    public VoiceSkillPage[] voiceSkillPages;
    public VoiceCategoryRow[] voiceFixedRows;
    sealed class SkillVoiceRow
    {
        public VoiceSkillPage page;
        public GameObject root;
        public Button selectSkill;
        public Text skillName;
        public VoiceCategoryRow audio;
    }
    readonly List<SkillVoiceRow> skillVoiceRows = new List<SkillVoiceRow>();
    readonly Dictionary<string, CharacterIdentityCatalog.NativeVoiceEntry> sourceVoiceEntries = new Dictionary<string, CharacterIdentityCatalog.NativeVoiceEntry>();
    bool voiceSkillPagesReady;
    int mixPreviewIndex;

    void InitializeVoiceSkillPages()
    {
        if (voiceSkillPagesReady) return;
        voiceSkillPagesReady = true;
        if (voiceSkillPages != null)
            foreach (var page in voiceSkillPages)
            {
                if (page.template != null) page.template.SetActive(false);
                if (page.add != null) page.add.onClick.AddListener(() => AddVoiceSkillRow(page, null));
            }
        if (voiceFixedRows != null)
            foreach (var row in voiceFixedRows)
            {
                if (row.selectVoiceButton != null) row.selectVoiceButton.onClick.AddListener(async () => await SelectVoiceFilesAsync(row));
                if (row.preview == null) continue;
                if (row.category == "wmm") row.preview.onClick.AddListener(async () => await PreviewVoiceAsync((mixPreviewIndex++ % 2 == 0) ? "wmm" : "wmt"));
                else row.preview.onClick.AddListener(async () => await PreviewVoiceAsync(row.category));
            }
    }

    static string VoiceLocale()
    {
        string locale = LocalizationManager.NowLoc;
        if (locale == "zh-tw" || locale == "zhtw") return "zh_hant";
        if (locale == "zh-cn" || locale == "zhcn") return "zh_hans";
        if (locale == "jp") return "ja";
        return CharacterIdentityCatalog.Locales.Contains(locale) ? locale : "en";
    }

    void RebuildVoiceSkillRows()
    {
        if (!voiceSkillPagesReady || manager == null) return;
        foreach (var row in skillVoiceRows.ToArray()) RemoveVoiceSkillRow(row, false);
        sourceVoiceEntries.Clear();
        if (voiceSkillPages == null) return;
        foreach (var row in voiceFixedRows ?? Array.Empty<VoiceCategoryRow>()) row.sourceCueNames = null;
        try
        {
            string avatar = string.IsNullOrWhiteSpace(nowCharacterData.voiceAvatarCode) ? null :
                AvatarRecipeParser.ResolveVoiceName(AvatarRecipeParser.Parse(nowCharacterData.voiceAvatarCode));
            string bank = VoicePreviewAudio.ResolveBank(DumpDataManager.CommonPath,
                string.IsNullOrWhiteSpace(nowCharacterData.voiceCharacterId) ? nowCharacterData.TemplateId : nowCharacterData.voiceCharacterId, avatar);
            foreach (var entry in CharacterIdentityCatalog.Get().NativeVoiceEntries(bank, VoiceLocale())) sourceVoiceEntries[entry.Definition.category] = entry;
            string[] nativeNames = new VoiceCueBank(System.IO.Path.ChangeExtension(bank, ".acb")).AvailableCueNames();
            foreach (var row in voiceFixedRows ?? Array.Empty<VoiceCategoryRow>())
                row.sourceCueNames = nativeNames.Where(name => NormalVoiceCategories.Matches(row.category, name.Substring(name.IndexOf('_') + 1))).ToArray();
        }
        catch (Exception error) { Debug.LogWarning("Cannot list source skill voices: " + error.Message, this); }
        var overrides = nowCharacterData.voiceOverrides ?? new List<CharacterVoiceOverride>();
        foreach (var entry in sourceVoiceEntries.Values)
        {
            var saved = overrides.Find(v => v.category == entry.Definition.category);
            if (saved?.muted == true) continue;
            var page = voiceSkillPages.FirstOrDefault(p => p.page == entry.Definition.page);
            if (page != null) AddVoiceSkillRow(page, saved?.files?.Count > 0 ? saved : entry.Definition);
        }
        foreach (var saved in overrides.ToArray())
        {
            var page = voiceSkillPages.FirstOrDefault(p => p.page == saved.page);
            if (page != null && saved.category?.StartsWith("cue:", StringComparison.Ordinal) == true && saved.files?.Count > 0 &&
                !saved.muted && !sourceVoiceEntries.ContainsKey(saved.category))
                AddVoiceSkillRow(page, saved);
        }
        RefreshVoiceFileLabels();
    }

    void AddVoiceSkillRow(VoiceSkillPage page, CharacterVoiceOverride saved)
    {
        if (page.template == null || page.content == null) return;
        var source = page.template.transform.Find("skill");
        if (source == null) { Debug.LogWarning("Voice Temp is missing its skill row.", this); return; }
        var root = Instantiate(source.gameObject, page.content, false);
        root.name = source.name; root.SetActive(true);
        var skill = root.transform;
        var select = skill.Find("SelectSkillButton").GetComponent<Button>();
        var upload = skill.Find("SelectVoiceButton").GetComponent<Button>();
        var preview = skill.Find("Preview").GetComponent<Button>();
        var remove = skill.Find("Close").GetComponent<Button>();
        var row = new SkillVoiceRow {
            page = page, root = root, selectSkill = select, skillName = select.GetComponentInChildren<Text>(true),
            audio = new VoiceCategoryRow {
                category = saved?.category, page = page.page, skillId = saved?.skillId ?? 0, skillResource = saved?.skillResource,
                preview = preview, selectVoiceButton = upload, fileNames = upload.GetComponentInChildren<Text>(true)
            }
        };
        if (row.audio.category != null && sourceVoiceEntries.TryGetValue(row.audio.category, out var native))
        { row.audio.inherited = true; row.audio.sourceCueNames = native.CueNames; }
        skillVoiceRows.Add(row);
        select.onClick.AddListener(() => SelectVoiceSkill(row));
        preview.onClick.AddListener(async () => await PreviewVoiceAsync(row.audio.category));
        upload.onClick.AddListener(async () => await SelectVoiceFilesAsync(row.audio));
        remove.onClick.AddListener(() => { StopVoicePreview(); RemoveVoiceSkillRow(row, true); });
        RefreshSkillVoiceRow(row); RefreshVoiceFileLabels(); LayoutVoiceRows(page);
    }

    void SelectVoiceSkill(SkillVoiceRow row)
    {
        var picker = manager.infoPage.skillPanel;
        if (picker == null) return;
        try
        {
            var catalog = CharacterIdentityCatalog.Get();
            var choices = catalog.SkillOptions(VoiceLocale()).Where(o => row.page.page == 5 ? o.Category == 4 || o.Category == 6 : o.Category == row.page.page - 1)
                .Where(o => catalog.VoiceCueResource(o) != null)
                .GroupBy(o => catalog.VoiceCueResource(o)).Select(g => g.First()).ToList();
            int revision = voiceSelectionRevision;
            picker.OpenVoiceSelection(catalog, VoiceLocale(), choices,
                () => this != null && !row.audio.removed && voicePanel != null && voicePanel.activeInHierarchy && revision == voiceSelectionRevision,
                option => {
                    if (row.audio.removed || revision != voiceSelectionRevision) return;
                    string resource = catalog.VoiceCueResource(option), category = "cue:" + resource;
                    if (skillVoiceRows.Any(r => r != row && !r.audio.removed && r.audio.category == category))
                    { Debug.LogWarning("Voice skill already selected: " + option.Label, this); return; }
                    var previous = nowCharacterData.voiceOverrides?.Find(v => v.category == row.audio.category);
                    if (previous != null) nowCharacterData.voiceOverrides.Remove(previous);
                    row.audio.category = category; row.audio.skillId = option.Id; row.audio.skillResource = option.Resource;
                    row.audio.inherited = sourceVoiceEntries.TryGetValue(category, out var native);
                    if (nowCharacterData.voiceOverrides?.Any(v => v.category == category && v.muted) == true) row.audio.inherited = false;
                    row.audio.sourceCueNames = native?.CueNames;
                    if (previous?.files?.Count > 0)
                    {
                        previous.category = category; previous.skillId = option.Id; previous.skillResource = option.Resource; previous.page = row.page.page;
                        nowCharacterData.voiceOverrides.Add(previous);
                    }
                    StopVoicePreview(); RefreshSkillVoiceRow(row); RefreshVoiceFileLabels();
                });
        }
        catch (Exception error) { Debug.LogWarning("Voice skill selection: " + error.Message, this); }
    }

    void RefreshSkillVoiceRow(SkillVoiceRow row)
    {
        bool selected = !string.IsNullOrEmpty(row.audio.category);
        row.audio.preview.interactable = selected; row.audio.selectVoiceButton.interactable = selected;
        var option = selected ? CharacterIdentityCatalog.Get().SkillOptions(VoiceLocale()).FirstOrDefault(o => o.Id == row.audio.skillId) : null;
        if (row.skillName != null) row.skillName.text = option?.Label ?? row.audio.skillResource ?? LocalizationManager.GetText("SkillNone");
    }

    void RemoveVoiceSkillRow(SkillVoiceRow row, bool removeSetting)
    {
        row.audio.removed = true;
        if (removeSetting && !string.IsNullOrEmpty(row.audio.category))
        {
            nowCharacterData.voiceOverrides ??= new List<CharacterVoiceOverride>();
            nowCharacterData.voiceOverrides.RemoveAll(v => v.category == row.audio.category);
            if (sourceVoiceEntries.ContainsKey(row.audio.category))
                nowCharacterData.voiceOverrides.Add(new CharacterVoiceOverride { category = row.audio.category, page = row.audio.page,
                    skillId = row.audio.skillId, skillResource = row.audio.skillResource, muted = true });
        }
        skillVoiceRows.Remove(row);
        if (row.root != null) { row.root.SetActive(false); row.root.transform.SetParent(null, false); CharacterManager.DestroyRuntimeObject(row.root); }
        LayoutVoiceRows(row.page);
    }

    void CleanupEmptyVoiceSkillRows()
    {
        if (manager == null) return;
        foreach (var row in skillVoiceRows.ToArray())
            if (!row.audio.inherited && (string.IsNullOrEmpty(row.audio.category) || nowCharacterData.voiceOverrides?.Find(v => v.category == row.audio.category)?.files?.Count > 0 != true))
                RemoveVoiceSkillRow(row, true);
        nowCharacterData.voiceOverrides?.RemoveAll(v => v.page >= 1 && !v.muted && (v.files == null || v.files.Count == 0));
    }

    void LayoutVoiceRows(VoiceSkillPage page)
    {
        if (page.content == null) return;
        var content = page.content as RectTransform;
        if (page.content.GetComponent<LayoutGroup>() == null && content != null)
        {
            float height = (page.template.transform.Find("skill") as RectTransform)?.rect.height ?? 30;
            if (height <= 0) height = 30;
            int index = 0;
            foreach (var row in skillVoiceRows.Where(r => r.page == page))
            {
                var rect = row.root.transform as RectTransform;
                rect.anchoredPosition = new Vector2(rect.anchoredPosition.x, -(index++ + rect.pivot.y) * height);
            }
            content.SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, index * height);
        }
        if (content != null) LayoutRebuilder.ForceRebuildLayoutImmediate(content);
    }

    IEnumerable<VoiceCategoryRow> AllVoiceRows() => (voiceCategoryRows ?? Array.Empty<VoiceCategoryRow>())
        .Concat(voiceFixedRows ?? Array.Empty<VoiceCategoryRow>()).Concat(skillVoiceRows.Where(r => !r.audio.removed).Select(r => r.audio));
}
