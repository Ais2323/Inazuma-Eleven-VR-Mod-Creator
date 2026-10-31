using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

// Uses the scene-authored SkillPanel, including its fixed page of item buttons.
public sealed class CharacterSkillPanelController : MonoBehaviour
{
    public Transform categoryParent, itemsParent;
    public GameObject selectItems, specifyItem;
    public InputField searchInput, pageInput;
    public GameObject modeNameParent;
    public InputField modeNameInput;
    public Transform modeLocaleButtonsParent;
    public Button previousPage, nextPage, existingSetting, newCharacterSetting, bindSkill, closeButton;
    public Sprite[] affinitySprites;
    readonly List<Button> categories = new(), items = new();
    readonly List<Button> modeLocaleButtons = new();
    CharacterIdentityCatalog catalog;
    List<CharacterIdentityCatalog.SkillOption> choices, filtered;
    CharacterTechniqueSettings technique;
    CharacterSkillTransformation draft;
    Func<bool> isCurrent;
    Action changed;
    string locale, modeLocale;
    int slot, category, page, bindCategory;
    enum Selection { Skill, Existing, Bind }
    Selection selection;
    bool initialized;

    void Initialize()
    {
        if (initialized) return;
        initialized = true;
        foreach (Transform child in categoryParent) {
            var button = child.GetComponent<Button>(); if (button == null) continue;
            int index = categories.Count; categories.Add(button); button.onClick.AddListener(() => SelectCategory(index));
        }
        foreach (Transform child in itemsParent) {
            var button = child.GetComponent<Button>(); if (button == null) continue;
            int index = items.Count; items.Add(button); button.onClick.AddListener(() => SelectItem(index));
        }
        searchInput.onValueChanged.AddListener(_ => { page = 0; RefreshItems(); });
        previousPage.onClick.AddListener(() => { page--; RefreshItems(); });
        nextPage.onClick.AddListener(() => { page++; RefreshItems(); });
        pageInput.contentType = InputField.ContentType.IntegerNumber;
        pageInput.onEndEdit.AddListener(s => { if (int.TryParse(s, out int value)) page = value - 1; RefreshItems(); });
        existingSetting.onClick.AddListener(() => {
            // Existing settings are a constrained sub-picker; categories must not change its purpose.
            selection = Selection.Existing; categoryParent.gameObject.SetActive(false);
            selectItems.SetActive(true); specifyItem.SetActive(false); page = 0; searchInput.SetTextWithoutNotify(""); RefreshItems();
        });
        newCharacterSetting.onClick.AddListener(SelectCharacterFile);
        foreach (Transform child in modeLocaleButtonsParent) {
            var button = child.GetComponent<Button>(); if (button == null) continue;
            string language = ModeLocale(child.name);
            button.onClick.AddListener(() => { modeLocale = language; RefreshModeName(); });
            modeLocaleButtons.Add(button);
        }
        modeNameInput.onValueChanged.AddListener(value => {
            if (category != 8 || draft == null || isCurrent?.Invoke() != true) return;
            draft.SetModeName(modeLocale, value); Commit(draft.auraId, draft, false);
        });
        modeNameInput.onEndEdit.AddListener(value => {
            if (category != 8 || draft == null || !string.IsNullOrWhiteSpace(value)) return;
            draft.SetModeName(modeLocale, ""); RefreshModeName(); Commit(draft.auraId, draft, false);
        });
        bindSkill.onClick.AddListener(() => {
            if (category != 5 || draft == null) return;
            selection = Selection.Bind; selectItems.SetActive(true); specifyItem.SetActive(false);
            bindCategory = 0;
            for (int i = 0; i < categories.Count; i++) categories[i].interactable = i != bindCategory;
            page = 0; searchInput.SetTextWithoutNotify(""); RefreshItems();
        });
        closeButton.onClick.AddListener(Close);
    }

    internal void Open(CharacterIdentityCatalog source, string locale, CharacterTechniqueSettings target, int index, Func<bool> current, Action onChanged)
    {
        if (source == null || target?.skills?.Length != 9 || index < 0 || index >= 9) return;
        Initialize(); catalog = source; this.locale = locale; modeLocale = locale; choices = catalog.SkillOptions(locale); technique = target; slot = index;
        isCurrent = current; changed = onChanged; gameObject.SetActive(true);
        int initial = choices.FirstOrDefault(c => c.Id == target.skills[index])?.Category ?? 0;
        // Opening the selector never changes a saved skill, including the single kizuna entry.
        ShowCategory(initial < 0 ? 0 : initial);
    }

    void SelectCategory(int index)
    {
        if (selection == Selection.Bind && index <= 3) {
            bindCategory = index; page = 0; searchInput.SetTextWithoutNotify("");
            for (int i = 0; i < categories.Count; i++) categories[i].interactable = i != index;
            RefreshItems(); return;
        }
        // Aura category clicks leave Bind mode; ordinary categories filter the bound technique.
        ShowCategory(index);
        if (index == 9) {
            var kizuna = choices.Single(c => c.Category == 9 && c.Resource == "wkt00010");
            Commit(kizuna.Id, null);
        }
    }
    void ShowCategory(int index)
    {
        category = index; selection = Selection.Skill; draft = null; page = 0;
        categoryParent.gameObject.SetActive(true); searchInput.SetTextWithoutNotify("");
        for (int i = 0; i < categories.Count; i++) categories[i].interactable = i != index;
        bool specify = index == 5 || index == 8;
        specifyItem.SetActive(specify); selectItems.SetActive(!specify); bindSkill.gameObject.SetActive(index == 5);
        bindSkill.interactable = false;
        var saved = technique.transformations?.FirstOrDefault(t => t.slot == slot && t.category == index);
        if (saved != null) {
            draft = JsonUtility.FromJson<CharacterSkillTransformation>(JsonUtility.ToJson(saved));
            bindSkill.interactable = index == 5;
        }
        else if (index == 5) {
            var current = choices.FirstOrDefault(c => c.Id == technique.skills[slot] && c.Category == 5);
            if (current != null) { draft = ExistingDraft(current); bindSkill.interactable = true; }
        }
        RefreshModeName(); RefreshBindLabel(); if (!specify) RefreshItems();
    }
    void RefreshModeName()
    {
        modeNameParent.SetActive(category == 8);
        modeNameInput.interactable = category == 8 && draft != null;
        foreach (var button in modeLocaleButtons) button.interactable = category == 8 && ModeLocale(button.name) != modeLocale;
        modeNameInput.SetTextWithoutNotify(category == 8 ? draft?.ModeNameForLocale(modeLocale) ?? "" : "");
    }
    static string ModeLocale(string value) => value == "jp" ? "ja" : value == "zhcn" ? "zh_hans" : value == "zhtw" ? "zh_hant" : value;
    void RefreshItems()
    {
        string search = searchInput.text.Trim();
        IEnumerable<CharacterIdentityCatalog.SkillOption> values = choices.Where(c => c.Category == category);
        if (selection == Selection.Bind) values = choices.Where(c => c.Category == bindCategory);
        // Hide development-only aura commands while keeping native playable variants.
        values = values.Where(c => c.Resource == null || !c.Resource.StartsWith("test_", StringComparison.Ordinal) && !c.Resource.StartsWith("pv8_", StringComparison.Ordinal));
        if (selection != Selection.Bind && selection != Selection.Existing)
            values = choices.Where(c => c.Id == 0).Concat(values);
        filtered = values.Where(c => search.Length == 0 || c.Label.IndexOf(search, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
            (c.Resource?.IndexOf(search, StringComparison.OrdinalIgnoreCase) ?? -1) >= 0).ToList();
        int count = Math.Max(1, (filtered.Count + items.Count - 1) / items.Count);
        page = Math.Max(0, Math.Min(page, count - 1));
        for (int i = 0; i < items.Count; i++) {
            int at = page * items.Count + i; var button = items[i]; bool valid = at < filtered.Count;
            button.gameObject.SetActive(valid); if (!valid) continue;
            var option = filtered[at]; button.GetComponentInChildren<Text>(true).text = option.Label;
            button.interactable = selection == Selection.Bind || option.Id != technique.skills[slot] ||
                technique.transformations?.Any(t => t.slot == slot) == true;
            var image = button.transform.Find("Affinity")?.GetComponent<Image>();
            if (image != null) {
                image.sprite = option.Affinity >= 0 && option.Affinity < affinitySprites.Length ? affinitySprites[option.Affinity] : null;
                image.enabled = image.sprite != null && option.Id != 0;
            }
        }
        previousPage.interactable = page > 0; nextPage.interactable = page + 1 < count;
        pageInput.SetTextWithoutNotify((page + 1).ToString(CultureInfo.InvariantCulture));
    }
    void SelectItem(int index)
    {
        int at = page * items.Count + index;
        if (filtered == null || at < 0 || at >= filtered.Count || isCurrent?.Invoke() != true) { Close(); return; }
        var option = filtered[at];
        if (selection == Selection.Bind) {
            if (category != 5 || draft == null || option.Category < 0 || option.Category > 3) return;
            draft.bindSkill = option.Id; Commit(draft.auraId, draft); return;
        }
        if (selection == Selection.Existing && category == 5) {
            Commit(option.Id, null, false);
            draft = ExistingDraft(option); selection = Selection.Skill;
            categoryParent.gameObject.SetActive(true); selectItems.SetActive(false); specifyItem.SetActive(true);
            bindSkill.interactable = true; RefreshBindLabel(); return;
        }
        Commit(option.Id, null);
    }
    CharacterSkillTransformation ExistingDraft(CharacterIdentityCatalog.SkillOption option) => new CharacterSkillTransformation {
        slot = slot, category = 5, auraId = option.Id, useExistingModel = true, characterName = option.Label,
        bindSkill = catalog.AuraTable.Rows.Single(r => r.Name == "AURA_CMD_INFO" && r.UInt(0) == option.Id).UInt(6)
    };
    void SelectCharacterFile()
    {
        if (category != 5 && category != 8) return;
        string[] paths = SFB.StandaloneFileBrowser.OpenFilePanel(LocalizationManager.GetText("BindNewCharacter"), "",
            new[] { new SFB.ExtensionFilter("CharacterMaker", "charamaker") }, false);
        if (paths == null || paths.Length == 0) return;
        try {
            var doc = CharacterMakerDocument.Read(paths[0]);
            if (doc.IsNew && string.IsNullOrWhiteSpace(doc.newCharacterKey) || !doc.IsNew && string.IsNullOrWhiteSpace(doc.targetId))
                throw new InvalidDataException(LocalizationManager.GetText("SkillInvalidCharacter"));
            var template = choices.Where(c => c.Category == category && (c.Resource?.StartsWith(category == 5 ? "wmm" : "mode_change_", StringComparison.Ordinal) ?? false))
                .OrderBy(c => c.Resource, StringComparer.Ordinal).First();
            string characterName = doc.identity?.locales?.FirstOrDefault(x => x.locale == locale && !string.IsNullOrWhiteSpace(x.fullName))?.fullName
                ?? doc.identity?.locales?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.fullName))?.fullName
                ?? Path.GetFileNameWithoutExtension(paths[0]);
            draft = new CharacterSkillTransformation { slot = slot, category = category, auraId = template.Id,
                characterKey = doc.IsNew ? doc.newCharacterKey : null, characterId = doc.IsNew ? null : doc.targetId,
                characterName = characterName,
                bindSkill = category == 5 ? catalog.AuraTable.Rows.Single(r => r.Name == "AURA_CMD_INFO" && r.UInt(0) == template.Id).UInt(6) : 0 };
            if (category == 8) foreach (string language in CharacterIdentityCatalog.Locales) {
                string name = doc.identity?.locales?.FirstOrDefault(x => x.locale == language && !string.IsNullOrWhiteSpace(x.fullName))?.fullName ?? characterName;
                draft.modeNames.Add(new CharacterModeName { locale = language, name = name, defaultName = name });
            }
            if (category == 8) { Commit(template.Id, draft, false); RefreshModeName(); }
            else { bindSkill.interactable = true; RefreshBindLabel(); }
        } catch (Exception e) { Debug.LogError(e.Message); }
    }
    void RefreshBindLabel()
    {
        var text = bindSkill.GetComponentInChildren<Text>(true);
        if (text != null) text.text = draft == null ? LocalizationManager.GetText("SkillBind") :
            draft.characterName + " · " + (choices.FirstOrDefault(c => c.Id == draft.bindSkill)?.Label ?? LocalizationManager.GetText("SkillBind"));
    }
    void Commit(uint id, CharacterSkillTransformation transformation, bool close = true)
    {
        if (isCurrent?.Invoke() != true) { Close(); return; }
        technique.transformations ??= new List<CharacterSkillTransformation>();
        technique.transformations.RemoveAll(t => t.slot == slot);
        if (transformation != null) technique.transformations.Add(transformation);
        technique.skills[slot] = id; if (id != 0 && technique.levels[slot] < 1) technique.levels[slot] = 1;
        changed?.Invoke(); if (close) Close();
    }
    public void Close() { draft = null; selection = Selection.Skill; gameObject.SetActive(false); }
    void OnDisable() { draft = null; selection = Selection.Skill; }
    void Update() { if (isCurrent != null && !isCurrent() || UnityEngine.InputSystem.Keyboard.current?.escapeKey.wasPressedThisFrame == true) Close(); }
}
