using System;
using System.Collections.Generic;
using System.Linq;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

public partial class CharacterInfoController
{
    [Header("Character details")]
    public Transform localeButtonsParent, attributeButtonsParent;
    public InputField fullNameInput, shortNameInput, shirtNameInput, descriptionInput;
    public Dropdown teamDropdown;
    readonly List<Button> localeButtons = new();
    readonly Dictionary<string, List<Button>> detailButtons = new();
    CharacterIdentityCatalog identityCatalog;
    string detailLocale = "en", teamLocale;
    static readonly Dictionary<string, string> LocaleNames = new() {
        { "jp", "ja" }, { "zhcn", "zh_hans" }, { "zhtw", "zh_hant" }
    };
    static readonly int[] GenderValues = { 1, 2, 5 };
    CharacterIdentitySettings Identity => !string.IsNullOrWhiteSpace(nowCharacterData.id) &&
        string.Equals(nowCharacterData.identity?.sourceId, nowCharacterData.id, StringComparison.OrdinalIgnoreCase)
        ? nowCharacterData.identity : null;

    void InitializeDetails(bool dumpReady)
    {
        detailLocale = AppLocale();
        if (localeButtonsParent != null)
            foreach (Transform child in localeButtonsParent)
            {
                var button = child.GetComponent<Button>(); if (button == null) continue;
                string locale = LocaleNames.TryGetValue(child.name, out var mapped) ? mapped : child.name;
                button.onClick.AddListener(() => { detailLocale = locale; RefreshIdentityFields(); });
                localeButtons.Add(button);
            }
        foreach (string group in new[] { "Gender", "OriginGame", "AcademicYear", "Affinity", "Position", "SubPosition" })
        {
            var buttons = new List<Button>(); detailButtons[group] = buttons;
            Transform parent = attributeButtonsParent != null ? attributeButtonsParent.Find(group + "/btns") : null;
            if (parent == null) continue;
            foreach (Transform child in parent)
            {
                var button = child.GetComponent<Button>(); if (button == null) continue;
                int index = buttons.Count; string field = group;
                button.onClick.AddListener(() => SetAttribute(field, index)); buttons.Add(button);
            }
        }
        if (fullNameInput != null) fullNameInput.onValueChanged.AddListener(v => EditLocale(x => { x.fullName = v; x.japaneseName = x.locale == "ja" ? v : null; }));
        if (shortNameInput != null) shortNameInput.onValueChanged.AddListener(v => EditLocale(x => x.shortName = v));
        if (shirtNameInput != null) shirtNameInput.onValueChanged.AddListener(v => EditLocale(x => x.shirtName = v));
        if (descriptionInput != null)
        {
            descriptionInput.lineType = InputField.LineType.MultiLineNewline;
            descriptionInput.onValueChanged.AddListener(v => EditLocale(x =>
                x.description = v.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", "\\n")));
        }
        if (teamDropdown != null) teamDropdown.onValueChanged.AddListener(index => {
            if (Identity != null && identityCatalog != null && index >= 0 && index < identityCatalog.Teams.Count)
                Identity.team = identityCatalog.Teams[index].Id;
        });
        if (dumpReady)
            try { identityCatalog = CharacterIdentityCatalog.Get(); RefreshTeams(); }
            catch (Exception e) { Debug.LogWarning("人物資訊載入失敗：" + e.Message); }
        RefreshIdentityFields();
    }
    static string AppLocale()
    {
        string locale = LocalizationManager.NowLoc;
        if (locale == "zh-tw" || locale == "zhtw") return "zh_hant";
        if (locale == "zh-cn" || locale == "zhcn") return "zh_hans";
        if (locale == "jp") return "ja";
        return CharacterIdentityCatalog.Locales.Contains(locale) ? locale : "en";
    }
    void EditLocale(Action<CharacterLocalizedIdentity> edit)
    {
        var locale = Identity?.locales?.Find(x => x.locale == detailLocale);
        if (locale != null) edit(locale);
    }
    internal void LoadIdentityForCharacter(string id)
    {
        nowCharacterData.identity = null;
        try
        {
            identityCatalog = CharacterIdentityCatalog.Get(); RefreshTeams();
            nowCharacterData.identity = identityCatalog.Load(id);
        }
        catch (Exception e) { Debug.LogWarning("人物資訊載入失敗：" + e.Message); }
        SynchronizeBodyGender();
        RefreshIdentityFields();
    }
    internal void RestoreIdentity(CharacterIdentitySettings saved)
    {
        if (saved != null && string.Equals(saved.sourceId, nowCharacterData.id, StringComparison.OrdinalIgnoreCase))
        {
            nowCharacterData.identity = saved.Clone();
            foreach (var locale in Identity.locales)
                locale.japaneseName = locale.locale == "ja" ? locale.fullName : null;
        }
        RefreshIdentityFields();
    }
    void RefreshTeams()
    {
        if (teamDropdown == null || identityCatalog == null) return;
        string locale = AppLocale(); if (teamLocale == locale) return;
        teamDropdown.ClearOptions();
        teamDropdown.AddOptions(identityCatalog.Teams.Select(t => {
            string name = identityCatalog.TeamName(t, locale);
            return string.IsNullOrEmpty(name) ? t.Id.ToString("X8") : name;
        }).ToList());
        teamLocale = locale;
    }
    void SetAttribute(string group, int index)
    {
        if (Identity == null || identityCatalog == null) return;
        switch (group)
        {
            case "Gender":
                Identity.gender = GenderValues[index];
                nowCharacterData.chestSize = Identity.gender == 1 ? -1 : Math.Max(0, nowCharacterData.chestSize);
                SynchronizeBodyGender();
                break;
            case "OriginGame": Identity.sourceSeries = identityCatalog.Series[index]; break;
            case "AcademicYear": Identity.academicYear = identityCatalog.AcademicYears[index]; break;
            case "Affinity": Identity.affinity = index; break;
            case "Position": Identity.position = index + 1; break;
            case "SubPosition": Identity.subPosition = index + 1; break;
        }
        RefreshIdentityFields();
    }
    internal void RefreshIdentityFields()
    {
        var value = Identity?.locales?.Find(x => x.locale == detailLocale);
        SetInput(fullNameInput, value?.fullName); SetInput(shortNameInput, value?.shortName);
        SetInput(shirtNameInput, value?.shirtName);
        SetInput(descriptionInput, value?.description?.Replace("\\n", "\n"));
        foreach (Button button in localeButtons)
        {
            string locale = LocaleNames.TryGetValue(button.name, out var mapped) ? mapped : button.name;
            button.interactable = Identity != null && locale != detailLocale;
        }
        foreach (var pair in detailButtons)
        {
            int selected = -1;
            if (Identity != null && identityCatalog != null)
                switch (pair.Key)
                {
                    case "Gender": selected = Array.IndexOf(GenderValues, Identity.gender); break;
                    case "OriginGame": selected = Array.IndexOf(identityCatalog.Series, Identity.sourceSeries); break;
                    case "AcademicYear": selected = Array.IndexOf(identityCatalog.AcademicYears, Identity.academicYear); break;
                    case "Affinity": selected = Identity.affinity; break;
                    case "Position": selected = Identity.position - 1; break;
                    case "SubPosition": selected = Identity.subPosition - 1; break;
                }
            SetSelectedButton(pair.Value, selected);
            if (Identity == null) foreach (var button in pair.Value) button.interactable = false;
        }
        if (teamDropdown != null)
        {
            RefreshTeams(); teamDropdown.interactable = Identity != null && identityCatalog != null;
            int index = Identity == null || identityCatalog == null ? 0 : identityCatalog.Teams.FindIndex(t => t.Id == Identity.team);
            teamDropdown.SetValueWithoutNotify(Math.Max(0, index)); teamDropdown.RefreshShownValue();
        }
    }
    void SetInput(InputField input, string value)
    {
        if (input == null) return;
        input.interactable = Identity != null; input.SetTextWithoutNotify(value ?? "");
    }
}
