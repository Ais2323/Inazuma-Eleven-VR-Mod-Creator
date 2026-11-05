using System;
using System.Collections.Generic;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    public Transform bodySetting;
    public Image skinColor;

    readonly List<Button> bodyButtons = new(), collarButtons = new(), sleeveButtons = new(), hemButtons = new(), chestButtons = new();
    int bodyType = 1, collar, sleeve, hem, chestSize = -1;
    Color appliedSkinColor;
    CfgTable.Row CurrentBody => catalog?.BodyForType(bodyType);
    Color SelectedSkinColor => skinColor != null ? skinColor.color : new Color32(255, 228, 208, 255);

    void InitializeBodySettings()
    {
        appliedSkinColor = SelectedSkinColor;
        BindBodyButtons("BodyType", 8, bodyButtons, value =>
        {
            if (catalog == null || catalog.BodyForType(value + 1) == null) return;
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
        return catalog.Resolve(catalog.Entries[selected], side, role, CurrentBody, wantedSleeve, wantedHem, CurrentEdit?.Clothes ?? 0) != null;
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
        SetButtons(bodyButtons, bodyType - 1, i => catalog != null && catalog.BodyForType(i + 1) != null);
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
        if (model == null) return;
        foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            if (renderer.sharedMaterial.HasProperty("_SkinColor")) renderer.sharedMaterial.SetColor("_SkinColor", color);
    }
}
