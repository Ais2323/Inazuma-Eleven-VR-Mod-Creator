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
    readonly List<Dropdown> skillDropdowns = new();
    readonly List<InputField> skillLevels = new();
    List<CharacterIdentityCatalog.SkillOption> skillChoices;
    public Dropdown techniqueDropdown;
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
            var dropdown = child.GetComponentInChildren<Dropdown>(true); var level = child.GetComponentInChildren<InputField>(true);
            if (dropdown == null || level == null) throw new InvalidOperationException("Skill row is missing its dropdown or level: " + slot);
            // Scene rows are indexed 0..8; preserve six main and three alternate slots.
            dropdown.onValueChanged.AddListener(index => {
                var selected = SelectedTechnique;
                if (selected != null && skillChoices != null && index >= 0 && index < skillChoices.Count) {
                    selected.skills[slot] = skillChoices[index].Id;
                    if (selected.skills[slot] != 0 && selected.levels[slot] < 1) selected.levels[slot] = 1;
                    RefreshSkills();
                }
            });
            level.contentType = InputField.ContentType.IntegerNumber;
            level.onEndEdit.AddListener(text => {
                var selected = SelectedTechnique; if (selected == null) return;
                if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) && value >= 1 && value <= 99)
                    selected.levels[slot] = value;
                RefreshSkills();
            });
            skillDropdowns.Add(dropdown); skillLevels.Add(level);
        }
        if (skillDropdowns.Count != 9) throw new InvalidOperationException("Skills requires nine rows.");
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
        foreach (var dropdown in skillDropdowns) { dropdown.ClearOptions(); dropdown.AddOptions(skillChoices.Select(s => s.Label).ToList()); }
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
        for (int i = 0; i < skillDropdowns.Count; i++) {
            var dropdown = skillDropdowns[i]; var level = skillLevels[i];
            bool valid = selected?.skills?.Length == 9 && selected.levels?.Length == 9 && skillChoices != null;
            dropdown.interactable = valid; level.interactable = valid;
            if (!valid) { dropdown.SetValueWithoutNotify(0); level.SetTextWithoutNotify(""); continue; }
            uint id = selected.skills[i]; int index = skillChoices.FindIndex(c => c.Id == id);
            if (index < 0) {
                skillChoices.Add(new CharacterIdentityCatalog.SkillOption { Id = id, Label = "0x" + id.ToString("X8") }); index = skillChoices.Count - 1;
                foreach (var d in skillDropdowns) d.AddOptions(new List<string> { skillChoices[index].Label });
            }
            dropdown.SetValueWithoutNotify(index); dropdown.RefreshShownValue(); level.SetTextWithoutNotify(selected.levels[i].ToString(CultureInfo.InvariantCulture));
        }
    }
}
