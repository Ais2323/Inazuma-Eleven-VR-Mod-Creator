using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;
public partial class CharacterInfoController
{
    public Transform skillsParent;
    readonly List<Button> skillButtons = new();
    readonly List<InputField> skillLevels = new();
    List<CharacterIdentityCatalog.SkillOption> skillChoices;
    public Dropdown techniqueDropdown;
    public CharacterSkillPanelController skillPanel;
    public Transform buildTypeButtonsParent;
    readonly List<Button> buildTypeButtons = new();
    string skillsLocale;
    bool skillsBound, skillsBindingAttempted;
    CharacterTechniqueSettings SelectedTechnique => Identity?.techniques?.FirstOrDefault(t => t.parameterId == Identity.techniqueParameterId);
    void InitializeSkills(bool dumpReady)
    {
        if (skillsBound || skillsBindingAttempted) return;
        skillsBindingAttempted = true;
        if (skillsParent == null || techniqueDropdown == null)
        {
            Debug.LogError("Skills UI is not bound: assign skillsParent and techniqueDropdown in the scene's CharacterInfoController.");
            return;
        }
        foreach (Transform child in skillsParent.Cast<Transform>().Where(t => int.TryParse(t.name, out _)).OrderBy(t => int.Parse(t.name)))
        {
            if (!int.TryParse(child.name, out int slot) || slot < 0 || slot >= 9) continue;
            var button = child.GetComponentInChildren<Button>(true); var level = child.GetComponentInChildren<InputField>(true);
            if (button == null || level == null) throw new InvalidOperationException("Skill row is missing its button or level: " + slot);
            // Scene rows are indexed 0..8; preserve six main and three alternate slots.
            button.onClick.AddListener(() => {
                var selected = SelectedTechnique;
                if (selected == null || skillPanel == null) return;
                skillPanel.Open(identityCatalog, AppLocale(), selected, slot, () => ReferenceEquals(selected, SelectedTechnique), RefreshSkills);
            });
            level.contentType = InputField.ContentType.IntegerNumber;
            level.onEndEdit.AddListener(text => {
                var selected = SelectedTechnique; if (selected == null) return;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 1 && value <= 99)
                    selected.levels[slot] = value;
                RefreshSkills();
            });
            skillButtons.Add(button); skillLevels.Add(level);
        }
        if (skillButtons.Count != 9) throw new InvalidOperationException("Skills requires nine rows.");
        var selector = techniqueDropdown;
        selector.onValueChanged.AddListener(index => {
            if (Identity?.techniques != null && index >= 0 && index < Identity.techniques.Count) {
                Identity.techniqueParameterId = Identity.techniques[index].parameterId; RefreshSkills();
            }
        });
        if (buildTypeButtonsParent != null) {
            foreach (Transform child in buildTypeButtonsParent) {
                var button = child.GetComponent<Button>(); if (button == null) continue;
                int value = buildTypeButtons.Count;
                button.onClick.AddListener(() => {
                    var selected = SelectedTechnique;
                    if (selected == null || value > 5) return;
                    selected.buildType = value; selected.hasBuildType = true; RefreshSkills();
                });
                buildTypeButtons.Add(button);
            }
            if (buildTypeButtons.Count != 6) throw new InvalidOperationException("Build type requires six authored icon buttons.");
        }
        else Debug.LogError("Build type buttons are not bound in CharacterInfoController.");
        skillsBound = true;
        if (dumpReady) try { identityCatalog = CharacterIdentityCatalog.Get(); SetSkillChoices(); }
            catch (Exception e) { Debug.LogWarning("Skills: " + e.Message); }
        RefreshSkills();
    }
    void SetSkillChoices()
    {
        string locale = AppLocale();
        if (skillsLocale == locale && skillChoices != null) return;
        skillChoices = new List<CharacterIdentityCatalog.SkillOption>(identityCatalog.SkillOptions(locale)); skillsLocale = locale;
    }
    void RefreshSkills()
    {
        if (!skillsBound) { InitializeSkills(DumpDataManager.ValidateSavedPath()); return; }
        if (identityCatalog != null && skillChoices != null) SetSkillChoices();
        if (Identity != null && (Identity.techniques == null || Identity.techniques.Count == 0) && identityCatalog != null)
            Identity.techniques = identityCatalog.LoadTechniques(Identity.sourceId);
        if (Identity?.techniques != null && SelectedTechnique == null)
            Identity.techniqueParameterId = Identity.techniques.FirstOrDefault()?.parameterId ?? 0;
        if (techniqueDropdown != null) {
            techniqueDropdown.gameObject.SetActive(Identity?.techniques?.Count > 1);
            techniqueDropdown.ClearOptions();
            if (Identity?.techniques != null) techniqueDropdown.AddOptions(Identity.techniques.Select((t,i) =>
                LocalizationManager.GetText("TechniqueVariant") + " " + (i+1) + " · " + LocalizationManager.GetText("TechniqueRarity") + " " + t.rarity + " · " + t.parameterId.ToString("X8")).ToList());
            techniqueDropdown.SetValueWithoutNotify(Math.Max(0, Identity?.techniques?.FindIndex(t => t.parameterId == Identity.techniqueParameterId) ?? 0));
            techniqueDropdown.RefreshShownValue();
        }
        // Additive save fields: legacy saves inherit each native variant's own value.
        if (identityCatalog != null && Identity?.techniques != null && Identity.techniques.Any(t => !t.hasBuildType)) {
            var native = identityCatalog.LoadTechniques(Identity.sourceId);
            foreach (var variant in Identity.techniques.Where(t => !t.hasBuildType)) {
                var baseline = native.FirstOrDefault(t => t.parameterId == variant.parameterId);
                if (baseline == null) continue;
                variant.buildType = variant.originalBuildType = baseline.buildType; variant.hasBuildType = true;
            }
        }
        var selected = SelectedTechnique;
        SetSelectedButton(buildTypeButtons, selected?.hasBuildType == true ? selected.buildType : -1);
        if (selected == null || !selected.hasBuildType)
            foreach (var button in buildTypeButtons) button.interactable = false;
        for (int i = 0; i < skillButtons.Count; i++) {
            var button = skillButtons[i]; var level = skillLevels[i];
            bool valid = selected?.skills?.Length == 9 && selected.levels?.Length == 9 && skillChoices != null;
            button.interactable = valid; level.interactable = valid;
            var label = button.GetComponentInChildren<Text>(true);
            if (!valid) { if (label != null) label.text = LocalizationManager.GetText("SkillNone"); level.SetTextWithoutNotify(""); continue; }
            uint id = selected.skills[i]; int index = skillChoices.FindIndex(c => c.Id == id);
            if (index < 0) {
                skillChoices.Add(new CharacterIdentityCatalog.SkillOption { Id = id, Label = "0x" + id.ToString("X8") }); index = skillChoices.Count - 1;
            }
            var transformation = selected.transformations?.FirstOrDefault(t => t.slot == i);
            if (label != null) label.text = transformation == null ? skillChoices[index].Label : transformation.useExistingModel
                ? skillChoices[index].Label + " / " + (skillChoices.FirstOrDefault(c => c.Id == transformation.bindSkill)?.Label ?? "")
                : transformation.category == 5 ? identityCatalog.TransformationTitle(5, AppLocale(), transformation.characterName)
                : LocalizationManager.GetText("SkillModeChange") + " · " + transformation.ModeNameForLocale(AppLocale());
            level.SetTextWithoutNotify(selected.levels[i].ToString(CultureInfo.InvariantCulture));
        }
    }
}
