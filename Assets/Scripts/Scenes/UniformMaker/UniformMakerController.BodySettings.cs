using System;
using System.Collections.Generic;
using System.Linq;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    public Transform bodySetting;
    public Image skinColor;
    public Toggle shareAwayModelsToggle;
    public Button shareBodyTexturesButton;
    public Button shareAllVariantsButton;
    bool shareAwayModels;

    public void OnClickShareAllVariants()
    {
        if (CurrentEdit == null) return;
        UpdateNameColor();
        CloseFbxBindings();
        CloseModelSelect();
        string prefix = catalog.Entries[selected].Id + "/";
        string sourcePrefix = prefix + side + "/" + role + "/";
        string modelPrefix = prefix + (shareAwayModels ? 0 : side) + "/" + role + "/";
        var copies = new Dictionary<string, KitEdit>();
        KitEdit Find(string key, int attribute) => kitEdits.TryGetValue(key + attribute, out var edit) ? edit :
            kitEdits.TryGetValue(key + "-1", out var legacy) ? legacy : null;
        for (int attribute = 0; attribute < 18; attribute++) {
            var body = catalog.BodyByAttribute(attribute);
            if (body == null || body.Int(4) != attribute) continue;
            var source = Find(sourcePrefix, attribute) ?? new KitEdit();
            var models = Find(modelPrefix, attribute) ?? new KitEdit();
            // Retail alternate body attributes can inherit an already configured
            // model. Resolve that before making the destination record explicit.
            if (!HasKitModelChanges(models))
                models = NativeBodyTypes.EquivalentAttributes(body).Select(a => Find(modelPrefix, a))
                    .FirstOrDefault(e => e != null && HasKitModelChanges(e)) ?? models;
            var copy = CloneKitEdit(source, models);
            if (sourcePrefix == modelPrefix && !HasKitModelChanges(source)) {
                if (source.Textures.Count == 0)
                    foreach (var texture in models.Textures) copy.Textures.Add(texture.Key, texture.Value.Copy());
                if (source.Portraits.Count == 0 && source.PortraitMasks.Count == 0 && !source.InheritPortraitCollar && !source.InheritPortraitSleeve) {
                    copy.InheritPortraitCollar = models.InheritPortraitCollar; copy.InheritPortraitSleeve = models.InheritPortraitSleeve;
                    foreach (var portrait in models.Portraits) copy.Portraits.Add(portrait.Key, portrait.Value);
                    foreach (var mask in models.PortraitMasks) copy.PortraitMasks.Add(mask.Key, mask.Value);
                }
            }
            // Preserve source native defaults rather than the destination's
            // original keeper/away model when no explicit override was selected.
            if (copy.Clothes == 0) copy.Clothes = catalog.Entries[selected].Models[shareAwayModels ? 0 : side, role];
            if (copy.Gloves == 0) copy.Gloves = catalog.Entries[selected].Gloves[shareAwayModels ? 0 : side];
            copies.Add(attribute.ToString(), copy);
        }
        try {
            for (int targetSide = 0; targetSide < 2; targetSide++)
                for (int targetRole = 0; targetRole < 2; targetRole++) {
                    if (targetSide == side && targetRole == role) continue;
                    string targetPrefix = prefix + targetSide + "/" + targetRole + "/";
                    foreach (string key in kitEdits.Keys.Where(k => k.StartsWith(targetPrefix, StringComparison.Ordinal)).ToArray()) {
                        foreach (var texture in kitEdits[key].Textures.Values) texture.Release();
                        kitEdits.Remove(key);
                    }
                    foreach (var copy in copies) kitEdits.Add(targetPrefix + copy.Key, CloneKitEdit(copy.Value, copy.Value));
                }
        }
        finally { foreach (var copy in copies.Values) foreach (var texture in copy.Textures.Values) texture.Release(); }
        selections.Clear();
        RefreshButtons();
    }

    static bool HasKitModelChanges(KitEdit edit) => edit.Clothes != 0 || edit.Armband != 0 || edit.Gloves != 0 ||
        edit.Skin != 0 || edit.Number != 0 || edit.Name != 0 || edit.Fbx != null || edit.PartFbx.Count > 0 ||
        edit.HideSkin || edit.HideNumber || edit.HideArmband || edit.HideGloves || edit.HideName || edit.HideUpper || edit.HideLower;

    static KitEdit CloneKitEdit(KitEdit source, KitEdit models)
    {
        var copy = new KitEdit {
            Clothes = models.Clothes, Armband = models.Armband, Gloves = models.Gloves,
            Skin = models.Skin, Number = models.Number, Name = models.Name,
            HideSkin = models.HideSkin, HideNumber = models.HideNumber, HideArmband = models.HideArmband,
            HideGloves = models.HideGloves, HideName = models.HideName, HideUpper = models.HideUpper, HideLower = models.HideLower,
            Fbx = models.Fbx?.Clone(), HasNameColor = source.HasNameColor, NameColor = source.NameColor,
            InheritPortraitCollar = source.InheritPortraitCollar, InheritPortraitSleeve = source.InheritPortraitSleeve
        };
        foreach (var part in models.PartFbx) copy.PartFbx.Add(part.Key, part.Value.Clone());
        foreach (var texture in source.Textures) copy.Textures.Add(texture.Key, texture.Value.Copy());
        foreach (var portrait in source.Portraits) copy.Portraits.Add(portrait.Key, portrait.Value);
        foreach (var mask in source.PortraitMasks) copy.PortraitMasks.Add(mask.Key, mask.Value);
        return copy;
    }

    public void OnClickShareBodyTextures()
    {
        if (CurrentEdit == null) return;
        UpdateNameColor();
        var source = CurrentEdit;
        var editable = source.Textures.Where(p => !p.Key.StartsWith("Name/", StringComparison.Ordinal)).ToArray();
        bool shareNameColor = CanEditNameColor;
        if (editable.Length == 0 && !shareNameColor) return;
        foreach (var texture in editable) texture.Value.SharedAcrossBodies = true;
        for (int attribute = 0; attribute < 18; attribute++) {
            var body = catalog.BodyByAttribute(attribute);
            if (body == null || body.Int(4) != attribute || !NativeBodyTypes.AllowsGender(body, gender)) continue;
            string key = catalog.Entries[selected].Id + "/" + side + "/" + role + "/" + attribute;
            if (!kitEdits.TryGetValue(key, out var target)) kitEdits.Add(key, target = new KitEdit());
            if (ReferenceEquals(source, target)) continue;
            if (shareNameColor) {
                target.HasNameColor = source.HasNameColor; target.NameColor = source.NameColor;
            }
            if (editable.Length > 0) {
                foreach (var old in target.Textures.Keys.Where(k => !k.StartsWith("Name/", StringComparison.Ordinal)).ToArray()) {
                    target.Textures[old].Release(); target.Textures.Remove(old);
                }
                foreach (var texture in editable) target.Textures.Add(texture.Key, texture.Value.Copy());
            }
        }
        RefreshButtons();
        Debug.Log("[UniformMaker] Applied current kit textures and enabled name color to all body types of gender " + gender + ".", this);
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
        if (shareAllVariantsButton != null) shareAllVariantsButton.onClick.AddListener(OnClickShareAllVariants);
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
        if (shareAllVariantsButton != null) shareAllVariantsButton.interactable = catalog != null && selected >= 0;
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
