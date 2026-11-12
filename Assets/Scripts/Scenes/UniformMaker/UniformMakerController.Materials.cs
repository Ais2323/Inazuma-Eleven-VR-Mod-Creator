using System;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    static void EnsureSpmControl(Transform root)
    {
        if (root.Find("SpmTex") != null || root.Find("SkinTex") == null) return;
        var spm = Instantiate(root.Find("SkinTex").gameObject, root, false); spm.name = "SpmTex";
    }

    void ClearMaterialRows(PartControls controls)
    {
        foreach (var row in controls.ExtraRows) { if (row != null) row.SetActive(false); DestroyAsset(row); }
        controls.ExtraRows.Clear();
    }

    void RefreshMaterialRows(UniformPartKind kind, PartControls controls)
    {
        ClearMaterialRows(controls);
        if (kind == UniformPartKind.Name) {
            foreach (string control in TextureControls) {
                var button = controls.Root.Find(control)?.GetComponent<Button>();
                if (button == null) continue;
                button.onClick.RemoveAllListeners(); button.gameObject.SetActive(false);
            }
            return;
        }
        var sets = PartRenderers(kind).Select(renderer => TextureProperties.Select(property =>
            renderer.sharedMaterial.HasProperty(property) ? renderer.sharedMaterial.GetTexture(property) : null).ToArray())
            .Where(set => set.Any(EditableTexture)).GroupBy(set => string.Join("|", set.Select(t => EditableTexture(t) ? t.name : "")))
            .Select(group => group.First()).ToArray();
        for (int rowIndex = 0; rowIndex < Math.Max(1, sets.Length); rowIndex++)
        {
            var root = controls.Root;
            if (rowIndex > 0)
            {
                var row = Instantiate(controls.Root.gameObject, controls.Root.parent, false);
                row.name = kind + "Textures_" + rowIndex; row.transform.SetSiblingIndex(controls.Root.GetSiblingIndex() + rowIndex);
                controls.ExtraRows.Add(row); root = row.transform;
                var import = root.Find("ImportFbx"); if (import != null) import.gameObject.SetActive(false);
                var layout = root.GetComponent<HorizontalLayoutGroup>(); if (layout != null) layout.padding.bottom = 5;
                root.GetComponent<RectTransform>().SetSizeWithCurrentAnchors(RectTransform.Axis.Vertical, 100);
                var select = root.Find("Select");
                if (select != null) {
                    var button = select.GetComponent<Button>(); if (button != null) { button.onClick.RemoveAllListeners(); button.interactable = false; }
                    var image = select.Find("RawImage")?.GetComponent<RawImage>(); if (image != null) image.enabled = false;
                    AddTextureLabel(select, sets[rowIndex].FirstOrDefault(EditableTexture)?.name ?? "");
                }
            }
            var set = rowIndex < sets.Length ? sets[rowIndex] : new Texture[TextureProperties.Length];
            for (int channel = 0; channel < TextureControls.Length; channel++)
            {
                var button = root.Find(TextureControls[channel])?.GetComponent<Button>(); if (button == null) continue;
                var image = button.transform.Find("RawImage")?.GetComponent<RawImage>();
                var texture = EditableTexture(set[channel]) ? set[channel] : null;
                SetTextureImage(image, texture); button.gameObject.SetActive(texture != null); button.onClick.RemoveAllListeners();
                if (texture == null) continue;
                int captured = channel; string name = texture.name;
                button.onClick.AddListener(() => ImportPartTexture(kind, captured, name));
                AddTextureLabel(button.transform, name);
            }
        }
    }

    static bool EditableTexture(Texture texture) => texture is Texture2D && !string.IsNullOrEmpty(texture.name) &&
        !new[] { "white", "black", "gray", "bump" }.Contains(texture.name.ToLowerInvariant()) && !texture.name.StartsWith("Default", StringComparison.OrdinalIgnoreCase);

    void AddTextureLabel(Transform parent, string name)
    {
        var label = parent.Find("TextureName")?.GetComponent<Text>();
        if (label == null) {
            var go = new GameObject("TextureName", typeof(RectTransform), typeof(CanvasRenderer), typeof(Text)); go.transform.SetParent(parent, false);
            label = go.GetComponent<Text>(); label.font = modelPageInput.textComponent.font; label.fontSize = 12;
            label.alignment = TextAnchor.LowerCenter; label.color = Color.black; label.raycastTarget = false;
            label.resizeTextForBestFit = true; label.resizeTextMinSize = 8; label.resizeTextMaxSize = 12;
            var rect = (RectTransform)label.transform; rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one; rect.offsetMin = new Vector2(2, 0); rect.offsetMax = new Vector2(-2, -2);
        }
        label.text = name;
    }
}
