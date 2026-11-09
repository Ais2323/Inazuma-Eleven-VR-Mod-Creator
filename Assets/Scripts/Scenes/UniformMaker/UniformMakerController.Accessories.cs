using System;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    static void FilterLabels(GameObject root, bool name)
    {
        foreach (var filter in root.GetComponentsInChildren<MeshFilter>(true))
            filter.gameObject.SetActive(UniformLabelAsset.IsName(filter.name) == name);
    }
    uint PartOverride(UniformPartKind kind)
    {
        var edit = ModelEdit;
        if (edit == null) return 0;
        return kind == UniformPartKind.Name ? edit.Name : kind == UniformPartKind.Skin ? edit.Skin : kind == UniformPartKind.Number ? edit.Number :
            kind == UniformPartKind.Armband ? edit.Armband : kind == UniformPartKind.Gloves ? edit.Gloves : edit.Clothes;
    }

    bool PartHidden(UniformPartKind kind)
    {
        var edit = ModelEdit;
        return edit != null && (kind == UniformPartKind.Upper ? edit.HideUpper : kind == UniformPartKind.Lower ? edit.HideLower : kind == UniformPartKind.Name ? edit.HideName : kind == UniformPartKind.Skin ? edit.HideSkin : kind == UniformPartKind.Number ? edit.HideNumber :
            kind == UniformPartKind.Armband ? edit.HideArmband : kind == UniformPartKind.Gloves && edit.HideGloves);
    }

    NativeUniformFashion.Selection ResolveAccessory(UniformPartKind kind, NativeUniformFashion.Selection outfit)
    {
        if (PartHidden(kind)) return null;
        uint id = PartOverride(kind);
        return id == 0 ? outfit : NativeUniformFashion.Resolve(NativeUniformFashion.ModelName(id), CurrentBody);
    }

    internal void SetAccessory(UniformPartKind kind, uint id, bool hidden)
    {
        var edit = ModelEdit; if (edit == null) return;
        if (hidden) id = 0;
        edit.PartFbx.Remove(kind);
        if (kind == UniformPartKind.Upper || kind == UniformPartKind.Lower) {
            if (kind == UniformPartKind.Upper) edit.HideUpper = hidden; else edit.HideLower = hidden;
            if (!hidden) edit.Fbx = null;
        }
        else if (kind == UniformPartKind.Skin) { edit.Skin = id; edit.HideSkin = hidden; }
        else if (kind == UniformPartKind.Number) { edit.Number = id; edit.HideNumber = hidden; }
        else if (kind == UniformPartKind.Name) { edit.Name = id; edit.HideName = hidden; }
        else if (kind == UniformPartKind.Armband) { edit.Armband = id; edit.HideArmband = hidden; }
        else if (kind == UniformPartKind.Gloves) { edit.Gloves = id; edit.HideGloves = hidden; }
        else throw new ArgumentOutOfRangeException(nameof(kind));
        ClearPartEdits(edit, kind); RebuildBodyPreview();
    }

    static void SetOptionLabel(Button button, string text)
    {
        var label = button.transform.Find("OptionLabel")?.GetComponent<Text>();
        if (label == null) return;
        label.text = text ?? ""; label.gameObject.SetActive(!string.IsNullOrEmpty(text));
    }

    void SetNativeAccessoryVisibility(bool visible)
    {
        foreach (var root in new[] { skinModel, numberModel, nameModel, armbandModel, gloveModel })
            if (root != null) root.SetActive(visible);
    }
}
