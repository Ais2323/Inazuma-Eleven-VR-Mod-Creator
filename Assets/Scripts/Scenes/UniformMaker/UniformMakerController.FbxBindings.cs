using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    sealed class FbxMeshTarget
    {
        internal UniformPartKind Kind;
        internal int Slot;
        internal string Label, ModelName;
        internal Bounds Bounds;
    }
    readonly List<FbxMeshTarget> pendingMeshTargets = new();
    int[] pendingMeshChoices;

    void InitializeFbxMeshBindings(UniformFbxReader imported, UniformFbxModel settings)
    {
        pendingMeshTargets.Clear();
        var kinds = pendingFbxWhole ? (UniformPartKind[])Enum.GetValues(typeof(UniformPartKind)) : new[] { pendingFbxKind };
        foreach (var kind in kinds) {
            if (kind == UniformPartKind.Gloves && role != 1) continue;
            NativeUniformFashion.Selection template;
            try { template = FbxTemplate(kind); }
            catch (InvalidDataException) { continue; }
            string path = Path.Combine(DumpDataManager.CommonPath, "chr", template.Uniform);
            var parsed = G4ModelParser.Parse(File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")), path);
            foreach (var part in parsed.Parts.Where(p => p.Name != "collar_11" && UniformFbxWriter.MatchesPart(parsed, p.SourceMeshIndex, kind))) {
                var bounds = new Bounds(part.Positions[0], Vector3.zero);
                foreach (var point in part.Positions) bounds.Encapsulate(point);
                pendingMeshTargets.Add(new FbxMeshTarget { Kind = kind, Slot = part.SourceMeshIndex, Bounds = bounds,
                    ModelName = Path.GetFileNameWithoutExtension(template.Uniform),
                    Label = LocalizationManager.GetText("UniformFbxPart" + kind) + " / " + part.Name + " / " + parsed.Materials[part.Material].Name });
            }
        }
        if (pendingMeshTargets.Count == 0) throw new InvalidDataException("No target mesh slots for this body.");
        pendingMeshChoices = new int[imported.Parts.Length];
        var options = new List<string> { LocalizationManager.GetText("UniformFbxSkipMesh") };
        options.AddRange(pendingMeshTargets.Select(t => t.Label));
        for (int i = 0; i < imported.Parts.Length; i++) {
            var part = imported.Parts[i];
            UniformPartKind preferred = pendingFbxKind;
            int separator = part.Name.IndexOf("__", StringComparison.Ordinal);
            bool namedPart = separator > 0 && Enum.TryParse(part.Name.Substring(0, separator), out preferred);
            if (!namedPart) preferred = pendingFbxKind;
            var candidates = pendingFbxWhole && !namedPart
                ? pendingMeshTargets.Where(t => t.Kind == UniformPartKind.Upper || t.Kind == UniformPartKind.Lower).ToArray()
                : pendingMeshTargets.Where(t => t.Kind == preferred).ToArray();
            // A named exported accessory that cannot be represented by this body
            // remains unselected until the user assigns it in the binding panel.
            if (candidates.Length == 0) pendingMeshChoices[i] = -1;
            else {
                var bounds = new Bounds(part.Positions[0], Vector3.zero);
                foreach (var point in part.Positions) bounds.Encapsulate(point);
                var exact = candidates.FirstOrDefault(t => part.Name.EndsWith(t.ModelName + "_" + t.Slot, StringComparison.OrdinalIgnoreCase));
                var chosen = exact ?? candidates.OrderBy(t => Vector3.SqrMagnitude(t.Bounds.center - bounds.center)).First();
                pendingMeshChoices[i] = pendingMeshTargets.IndexOf(chosen);
            }
            int index = i;
            var row = Instantiate(fbxMeshTemplate, fbxMeshContent, false); row.SetActive(true); fbxRows.Add(row);
            row.transform.Find("SourceName").GetComponent<Text>().text = part.Name;
            var dropdown = row.transform.Find("Target").GetComponent<Dropdown>();
            dropdown.ClearOptions(); dropdown.AddOptions(options); dropdown.SetValueWithoutNotify(pendingMeshChoices[i] + 1);
            dropdown.onValueChanged.AddListener(value => { pendingMeshChoices[index] = value - 1; RefreshFbxStatus(); });
        }
    }

    Dictionary<UniformPartKind, UniformFbxModel> PendingFbxBindings()
    {
        var result = new Dictionary<UniformPartKind, UniformFbxModel>();
        foreach (var group in Enumerable.Range(0, pendingMeshChoices.Length).Where(i => pendingMeshChoices[i] >= 0)
            .GroupBy(i => pendingMeshTargets[pendingMeshChoices[i]].Kind)) {
            var settings = pendingFbxSettings.Clone();
            settings.meshIndices = group.ToArray();
            settings.meshSlots = group.Select(i => pendingMeshTargets[pendingMeshChoices[i]].Slot).ToArray();
            result.Add(group.Key, settings);
        }
        if (result.Count == 0) throw new InvalidDataException("Select at least one FBX mesh.");
        return result;
    }

    static UniformMakerVariant BoundVariant(Dictionary<UniformPartKind, UniformFbxModel> bindings) => new UniformMakerVariant {
        hideUpper = !bindings.ContainsKey(UniformPartKind.Upper), hideLower = !bindings.ContainsKey(UniformPartKind.Lower),
        accessoriesConfigured = true, labelsConfigured = true,
        partFbx = bindings.Select(p => new UniformMakerPartFbx { part = p.Key, model = p.Value }).ToList()
    };

    IEnumerable<UniformPartKind> BoundRoots(Dictionary<UniformPartKind, UniformFbxModel> bindings)
    {
        if (bindings.ContainsKey(UniformPartKind.Upper) || bindings.ContainsKey(UniformPartKind.Lower)) yield return UniformPartKind.Upper;
        foreach (var kind in bindings.Keys.Where(k => k != UniformPartKind.Upper && k != UniformPartKind.Lower)) yield return kind;
    }

    UniformFbxWriter.Result BuildBoundFbx(UniformPartKind kind, UniformMakerVariant variant, NativeUniformFashion.Selection template)
    {
        string path = Path.Combine(DumpDataManager.CommonPath, "chr", template.Uniform);
        return UniformFbxWriter.Apply(File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")),
            variant, kind, NativeBodyShape.Skeleton(CurrentBody));
    }

    void PreviewBoundFbx(Dictionary<UniformPartKind, UniformFbxModel> bindings)
    {
        var variant = BoundVariant(bindings);
        ReleaseFbxPreview();
        pendingFbxPreview = new GameObject("FBX outfit preview"); pendingFbxPreview.transform.SetParent(modelParent, false);
        foreach (var kind in BoundRoots(bindings)) {
            var template = FbxTemplate(kind);
            var generated = BuildBoundFbx(kind, variant, template);
            var part = G4ModelLoader.CreateModel(Path.Combine("chr", Path.GetDirectoryName(template.Uniform)),
                Path.GetFileNameWithoutExtension(template.Uniform), Path.GetFileNameWithoutExtension(template.UniformTexture),
                Path.Combine("chr", Path.GetDirectoryName(template.UniformTexture)), skeletonOverride: NativeBodyShape.Skeleton(CurrentBody),
                boneTransforms: NativeBodyShape.Outfit(CurrentBody, chestSize, hem), headLocalSkinning: false,
                metadataOverride: generated.Metadata, geometryOverride: generated.Geometry);
            part.transform.SetParent(pendingFbxPreview.transform, false);
            if (kind == UniformPartKind.Name || kind == UniformPartKind.Number) FilterLabels(part, kind == UniformPartKind.Name);
            foreach (var renderer in part.GetComponentsInChildren<Renderer>()) {
                renderer.sharedMaterial.SetColor("_SkinColor", SelectedSkinColor);
                if (kind == UniformPartKind.Skin) renderer.sharedMaterial.SetFloat("_WholeSkin", 1);
            }
        }
        if (model != null) model.SetActive(false);
        var renderers = pendingFbxPreview.GetComponentsInChildren<Renderer>();
        if (renderers.Length > 0) {
            var bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            preview.FrameBounds(bounds);
        }
    }

    void ApplyBoundFbx(Dictionary<UniformPartKind, UniformFbxModel> bindings)
    {
        var templates = BoundRoots(bindings).ToDictionary(k => k, FbxTemplate);
        var variant = BoundVariant(bindings);
        foreach (var template in templates) BuildBoundFbx(template.Key, variant, template.Value);
        var edit = ModelEdit;
        edit.Fbx = null; edit.PartFbx.Clear();
        edit.HideUpper = variant.hideUpper; edit.HideLower = variant.hideLower;
        edit.Skin = edit.Number = edit.Name = edit.Armband = edit.Gloves = 0;
        edit.HideSkin = edit.HideNumber = edit.HideName = edit.HideArmband = edit.HideGloves = true;
        foreach (var pair in templates.Where(p => p.Key != UniformPartKind.Upper)) AssignAccessory(pair.Key, pair.Value.ModelId, false);
        foreach (var pair in bindings) edit.PartFbx.Add(pair.Key, pair.Value.Clone());
        ClearPartEdits(CurrentEdit, UniformPartKind.Name, UniformPartKind.Skin, UniformPartKind.Number, UniformPartKind.Armband, UniformPartKind.Gloves);
    }
}
