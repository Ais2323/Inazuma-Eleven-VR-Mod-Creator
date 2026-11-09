using System;
using System.Collections.Generic;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    public Transform bodySetting;
    public Image skinColor;
    public Toggle shareAwayModelsToggle;
    public Button shareBodyTexturesButton;
    bool shareAwayModels;

    public void OnClickShareBodyTextures()
    {
        if (CurrentEdit == null) return;
        var source = CurrentEdit;
        foreach (var texture in source.Textures.Values) texture.SharedAcrossBodies = true;
        for (int attribute = 0; attribute < 18; attribute++) {
            if (catalog.BodyByAttribute(attribute) == null) continue;
            string key = catalog.Entries[selected].Id + "/" + side + "/" + role + "/" + attribute;
            if (!kitEdits.TryGetValue(key, out var target)) kitEdits.Add(key, target = new KitEdit());
            if (ReferenceEquals(source, target)) continue;
            foreach (var old in target.Textures.Values) old.Release();
            target.Textures.Clear();
            foreach (var texture in source.Textures) target.Textures.Add(texture.Key, texture.Value.Copy());
        }
        RefreshButtons();
        Debug.Log("[UniformMaker] Applied current kit textures to all body types.", this);
    }

    readonly List<Button> bodyButtons = new(), collarButtons = new(), sleeveButtons = new(), hemButtons = new(), chestButtons = new();
    readonly List<Button> genderButtons = new();
    int bodyType = 1, gender = 1, collar, sleeve, hem, chestSize = -1;
    Color appliedSkinColor;
    CfgTable.Row CurrentBody => catalog?.BodyForType(bodyType, gender);
    Color SelectedSkinColor => skinColor != null ? skinColor.color : new Color32(255, 228, 208, 255);

    void InitializeBodySettings()
    {
        if (shareBodyTexturesButton != null) shareBodyTexturesButton.onClick.AddListener(OnClickShareBodyTextures);
        if (shareAwayModelsToggle != null) shareAwayModelsToggle.onValueChanged.AddListener(value => {
            if (shareAwayModels == value) return;
            shareAwayModels = value; RebuildBodyPreview();
        });
        appliedSkinColor = SelectedSkinColor;
        BindBodyButtons("Gender", 2, genderButtons, value => {
            gender = value + 1;
            if (catalog != null && catalog.BodyForType(bodyType, gender) == null) bodyType = gender == 2 ? 2 : 1;
            RebuildBodyPreview();
        });
        BindBodyButtons("BodyType", 8, bodyButtons, value =>
        {
            if (catalog == null || catalog.BodyForType(value + 1, gender) == null) return;
            bodyType = value + 1;
            RebuildBodyPreview();
        });
        BindBodyButtons("UniformCollar", 2, collarButtons, value => { collar = value; RebuildBodyPreview(); });
        BindBodyButtons("UniformSleeve", 2, sleeveButtons, value =>
        {
            if (!CanUseFashion(value, 0)) return;
            sleeve = value;
            if (sleeve != 0 && hem == 2) hem = 0;
            RebuildBodyPreview();
        });
        BindBodyButtons("UniformHem", 3, hemButtons, value =>
        {
            if (!CanUseFashion(0, value)) return;
            hem = value;
            if (hem == 2) sleeve = 0;
            RebuildBodyPreview();
        });
        BindBodyButtons("ChestSize", 3, chestButtons, value => { chestSize = value; RebuildBodyPreview(); });
    }

    void BindBodyButtons(string name, int count, List<Button> buttons, Action<int> select)
    {
        if (bodySetting == null) return;
        var parent = bodySetting.Find(name + "/btns");
        if (parent == null) { Debug.LogWarning("[UniformMaker] Missing BodySetting/" + name + "/btns", this); return; }
        foreach (Transform child in parent)
        {
            var button = child.GetComponent<Button>();
            if (button == null) continue;
            int value = buttons.Count;
            button.onClick.AddListener(() => select(value));
            buttons.Add(button);
        }
        if (buttons.Count != count) Debug.LogWarning("[UniformMaker] " + name + " expected " + count + " buttons, found " + buttons.Count, this);
    }

    bool CanUseFashion(int wantedSleeve, int wantedHem)
    {
        if (CurrentBody == null || selected < 0 || !NativeUniformFashion.Allowed(CurrentBody, wantedSleeve, wantedHem)) return false;
        return catalog.Resolve(catalog.Entries[selected], side, role, CurrentBody, wantedSleeve, wantedHem, ModelEdit?.Clothes ?? 0) != null;
    }

    void NormalizeFashion()
    {
        if (!CanUseFashion(sleeve, 0)) sleeve = 0;
        if (!CanUseFashion(0, hem)) hem = 0;
        selections.Clear();
    }

    void RebuildBodyPreview()
    {
        NormalizeFashion();
        if (selected >= 0) LoadPreview();
        RefreshButtons();
    }

    void RefreshBodySettingButtons()
    {
        if (shareAwayModelsToggle != null) shareAwayModelsToggle.SetIsOnWithoutNotify(shareAwayModels);
        if (shareBodyTexturesButton != null) shareBodyTexturesButton.interactable = catalog != null && selected >= 0;
        SetButtons(genderButtons, gender - 1, _ => catalog != null);
        SetButtons(bodyButtons, bodyType - 1, i => catalog != null && catalog.BodyForType(i + 1, gender) != null);
        SetButtons(collarButtons, collar, _ => selected >= 0);
        SetButtons(sleeveButtons, sleeve, i => CanUseFashion(i, 0));
        SetButtons(hemButtons, hem, i => CanUseFashion(0, i));
        SetButtons(chestButtons, chestSize, _ => CurrentBody != null && selected >= 0);
    }

    static void SetButtons(List<Button> buttons, int selectedValue, Func<int, bool> available)
    {
        for (int i = 0; i < buttons.Count; i++)
        {
            buttons[i].interactable = i != selectedValue && available(i);
            if (buttons[i].image != null) buttons[i].image.color = i == selectedValue ? new Color(.55f, .55f, .55f, 1) : Color.white;
        }
    }

    void UpdateSkinColor()
    {
        Color color = SelectedSkinColor;
        if (color == appliedSkinColor) return;
        appliedSkinColor = color;
        RefreshPortraitIcon();
        if (model == null) return;
        foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            if (renderer.sharedMaterial.HasProperty("_SkinColor")) renderer.sharedMaterial.SetColor("_SkinColor", color);
    }
}
