using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G4Composition;
using G4Runtime;
using SFB;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    public Button fbxImportButton, fbxCloseButton, fbxApplyButton, fbxPreviewButton;
    public Transform fbxBindingPanel, fbxBoneContent, fbxMeshContent;
    public GameObject fbxBoneTemplate, fbxMeshTemplate;
    public InputField fbxScaleInput;
    public Toggle fbxFlipVToggle;
    public Text fbxStatus;
    bool pendingFbxWhole;
    UniformPartKind pendingFbxKind = UniformPartKind.Upper;
    UniformFbxReader pendingFbx;
    UniformFbxModel pendingFbxSettings;
    GameObject pendingFbxPreview;
    readonly List<GameObject> fbxRows = new();

    void InitializeFbxControls()
    {
        if (fbxImportButton != null) fbxImportButton.onClick.AddListener(OnClickImportFbx);
        if (fbxCloseButton != null) fbxCloseButton.onClick.AddListener(CloseFbxBindings);
        if (fbxApplyButton != null) fbxApplyButton.onClick.AddListener(OnClickApplyFbx);
        if (fbxPreviewButton != null) fbxPreviewButton.onClick.AddListener(OnClickPreviewFbx);
        if (fbxFlipVToggle != null) fbxFlipVToggle.onValueChanged.AddListener(value => {
            if (pendingFbxSettings != null) pendingFbxSettings.flipV = value;
        });
        if (fbxScaleInput != null) fbxScaleInput.onValueChanged.AddListener(value => {
            if (pendingFbxSettings == null) return;
            pendingFbxSettings.scale = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float scale) ? scale : 0;
            RefreshFbxStatus();
        });
    }

    public void OnClickImportFbx() => ImportFbxPart(UniformPartKind.Upper, true);

    void ImportFbxPart(UniformPartKind kind, bool whole = false)
    {
        if (CurrentBody == null || selected < 0) return;
        var files = StandaloneFileBrowser.OpenFilePanel("Import uniform FBX", "", "fbx", false);
        if (files == null || files.Length == 0 || string.IsNullOrWhiteSpace(files[0])) return;
        try { OpenFbxFile(files[0], kind, whole); } catch (Exception e) { Debug.LogError("[UniformMaker FBX] " + e.Message, this); }
    }

    internal void OpenFbxFile(string path, UniformPartKind kind = UniformPartKind.Upper, bool whole = true)
    {
        if (fbxBindingPanel == null || fbxBoneTemplate == null || fbxMeshTemplate == null) throw new InvalidOperationException("Missing FBX binding panel in the scene.");
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("FBX exceeds 64 MB.");
        byte[] bytes = File.ReadAllBytes(path); var imported = UniformFbxReader.Read(bytes);
        var rig = NativeBodyShape.Skeleton(CurrentBody);
        var settings = imported.BindAutomatically(bytes, Path.GetFileName(path), rig);
        var outfit = FbxTemplate(kind);
        byte[] metadata = File.ReadAllBytes(Path.Combine(DumpDataManager.CommonPath, "chr", outfit.Uniform));
        var target = G4ModelParser.Parse(metadata, File.ReadAllBytes(Path.ChangeExtension(Path.Combine(DumpDataManager.CommonPath, "chr", outfit.Uniform), ".g4mg")), outfit.Uniform);
        var slots = target.Parts.Where(p => p.Name != "collar_11" && (whole || UniformFbxWriter.MatchesPart(target, p.SourceMeshIndex, kind))).ToArray();
        if (slots.Length == 0) throw new InvalidDataException("No target mesh slots for " + kind + ": " + outfit.Uniform);
        for (int i = 0; i < imported.Parts.Length; i++) {
            var suffix = Regex.Match(imported.Parts[i].Name, @"^" + Regex.Escape(Path.GetFileNameWithoutExtension(outfit.Uniform)) + @"_(\d+)$", RegexOptions.IgnoreCase);
            if (suffix.Success && int.TryParse(suffix.Groups[1].Value, out int nativeSlot) && slots.Any(p => p.SourceMeshIndex == nativeSlot)) { settings.meshSlots[i] = nativeSlot; continue; }
            var positions = imported.Parts[i].Positions; var bounds = new Bounds(positions[0], Vector3.zero); foreach (var point in positions) bounds.Encapsulate(point);
            settings.meshSlots[i] = slots.OrderBy(p => {
                var b = new Bounds(p.Positions[0], Vector3.zero); foreach (var point in p.Positions) b.Encapsulate(point); return Vector3.SqrMagnitude(b.center - bounds.center);
            }).First().SourceMeshIndex;
        }
        CloseFbxBindings(); CloseModelSelect(); pendingFbx = imported; pendingFbxSettings = settings; pendingFbxKind = kind; pendingFbxWhole = whole;
        mainPanel.gameObject.SetActive(false); fbxBindingPanel.gameObject.SetActive(true);
        fbxScaleInput.SetTextWithoutNotify(settings.scale.ToString(CultureInfo.InvariantCulture));
        if (fbxFlipVToggle != null) fbxFlipVToggle.SetIsOnWithoutNotify(settings.flipV);
        var boneChoices = new List<string> { LocalizationManager.GetText("UniformFbxUnassigned") }; boneChoices.AddRange(rig.Names);
        for (int i = 0; i < imported.Bones.Length; i++) {
            int index = i; var row = Instantiate(fbxBoneTemplate, fbxBoneContent, false); row.SetActive(true); fbxRows.Add(row);
            row.transform.Find("SourceName").GetComponent<Text>().text = imported.Bones[i].Name;
            var dropdown = row.transform.Find("Target").GetComponent<Dropdown>(); dropdown.ClearOptions(); dropdown.AddOptions(boneChoices);
            dropdown.SetValueWithoutNotify(rig.ByHash.TryGetValue(settings.bones[i].targetHash, out int bone) ? bone + 1 : 0);
            dropdown.onValueChanged.AddListener(value => { settings.bones[index].targetHash = value == 0 ? 0 : rig.Hashes[value - 1]; RefreshFbxStatus(); });
        }
        var meshChoices = slots.Select(p => string.IsNullOrWhiteSpace(target.Materials[p.Material].Name) ? p.Name : p.Name + " / " + target.Materials[p.Material].Name).ToList();
        for (int i = 0; i < imported.Parts.Length; i++) {
            int index = i; var row = Instantiate(fbxMeshTemplate, fbxMeshContent, false); row.SetActive(true); fbxRows.Add(row);
            row.transform.Find("SourceName").GetComponent<Text>().text = imported.Parts[i].Name;
            var dropdown = row.transform.Find("Target").GetComponent<Dropdown>(); dropdown.ClearOptions(); dropdown.AddOptions(meshChoices);
            dropdown.SetValueWithoutNotify(Array.FindIndex(slots, p => p.SourceMeshIndex == settings.meshSlots[i]));
            dropdown.onValueChanged.AddListener(value => { settings.meshSlots[index] = slots[value].SourceMeshIndex; RefreshFbxStatus(); });
        }
        RefreshFbxStatus();
        if (settings.bones.All(b => b.targetHash != 0)) OnClickPreviewFbx();
    }

    void RefreshFbxStatus()
    {
        if (pendingFbxSettings == null) return;
        int mapped = pendingFbxSettings.bones.Count(b => b.targetHash != 0);
        bool complete = mapped == pendingFbxSettings.bones.Count && pendingFbxSettings.scale > 0 && !float.IsInfinity(pendingFbxSettings.scale) && !float.IsNaN(pendingFbxSettings.scale);
        fbxStatus.text = pendingFbxSettings.fileName + " — " + string.Format(LocalizationManager.GetText("UniformFbxMapped"), mapped, pendingFbxSettings.bones.Count);
        fbxApplyButton.interactable = fbxPreviewButton.interactable = complete;
    }

    NativeUniformFashion.Selection FbxTemplate(UniformPartKind kind)
    {
        var outfit = GetSelection(side, role);
        string mesh = outfit?.Uniform, texture = outfit?.UniformTexture;
        if (kind == UniformPartKind.Gloves) {
            var gloves = catalog.ResolveGloves(PartOverride(kind) != 0 ? PartOverride(kind) : catalog.Entries[selected].Gloves[shareAwayModels && side == 1 ? 0 : side], CurrentBody);
            mesh = gloves?.Model; texture = gloves?.Texture;
        }
        else if (kind != UniformPartKind.Upper && kind != UniformPartKind.Lower) {
            var chosen = PartOverride(kind) == 0 ? outfit : NativeUniformFashion.Resolve(NativeUniformFashion.ModelName(PartOverride(kind)), CurrentBody);
            mesh = kind == UniformPartKind.Skin ? chosen?.Skin : kind == UniformPartKind.Armband ? chosen?.Armband : chosen?.Number;
            texture = kind == UniformPartKind.Skin ? chosen?.SkinTexture : kind == UniformPartKind.Armband ? chosen?.ArmbandTexture : chosen?.NumberTexture;
            if (string.IsNullOrWhiteSpace(mesh) ||
                (kind == UniformPartKind.Name || kind == UniformPartKind.Number) && !UniformLabelAsset.HasPart(mesh, kind == UniformPartKind.Name)) {
                var option = catalog.OptionsFor(kind, CurrentBody).FirstOrDefault() ?? throw new InvalidDataException("No native material template for " + kind);
                chosen = NativeUniformFashion.Resolve(NativeUniformFashion.ModelName(option.Id), CurrentBody);
                mesh = kind == UniformPartKind.Skin ? chosen.Skin : kind == UniformPartKind.Armband ? chosen.Armband : chosen.Number;
                texture = kind == UniformPartKind.Skin ? chosen.SkinTexture : kind == UniformPartKind.Armband ? chosen.ArmbandTexture : chosen.NumberTexture;
            }
        }
        if (string.IsNullOrWhiteSpace(mesh) || string.IsNullOrWhiteSpace(texture)) throw new InvalidDataException("Missing FBX material template: " + kind);
        return new NativeUniformFashion.Selection { Uniform = mesh, UniformTexture = texture };
    }

    UniformMakerVariant GeometrySettings()
    {
        var edit = ModelEdit;
        return new UniformMakerVariant { fbx = edit?.Fbx, hideUpper = edit?.HideUpper ?? false, hideLower = edit?.HideLower ?? false,
            accessoriesConfigured = true, labelsConfigured = true,
            partFbx = edit?.PartFbx.Select(p => new UniformMakerPartFbx { part = p.Key, model = p.Value }).ToList() ?? new List<UniformMakerPartFbx>() };
    }

    UniformFbxWriter.Result BuildPartFbx(string mesh, UniformPartKind kind)
    {
        var settings = GeometrySettings();
        if (kind != UniformPartKind.Upper && !settings.partFbx.Any(p => p.part == kind)) return null;
        if (kind == UniformPartKind.Upper && settings.fbx == null && !settings.hideUpper && !settings.hideLower && !settings.partFbx.Any(p => p.part == UniformPartKind.Upper || p.part == UniformPartKind.Lower)) return null;
        string path = Path.Combine(DumpDataManager.CommonPath, "chr", mesh);
        return UniformFbxWriter.Apply(File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")), settings, kind, NativeBodyShape.Skeleton(CurrentBody));
    }

    UniformFbxWriter.Result BuildFbx(NativeUniformFashion.Selection outfit, UniformFbxModel settings)
    {
        string path = Path.Combine(DumpDataManager.CommonPath, "chr", outfit.Uniform);
        var parsed = G4ModelParser.Parse(File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")), path);
        return UniformFbxWriter.Build(File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")), settings, NativeBodyShape.Skeleton(CurrentBody), pendingFbxWhole ? null : (Func<int, bool>)(slot => UniformFbxWriter.MatchesPart(parsed, slot, pendingFbxKind)));
    }

    public void OnClickPreviewFbx()
    {
        if (pendingFbxSettings == null) return;
        try {
            var outfit = FbxTemplate(pendingFbxKind); var generated = BuildFbx(outfit, pendingFbxSettings);
            ReleaseFbxPreview();
            pendingFbxPreview = G4ModelLoader.CreateModel(Path.Combine("chr", Path.GetDirectoryName(outfit.Uniform)), Path.GetFileNameWithoutExtension(outfit.Uniform),
                Path.GetFileNameWithoutExtension(outfit.UniformTexture), Path.Combine("chr", Path.GetDirectoryName(outfit.UniformTexture)),
                skeletonOverride: NativeBodyShape.Skeleton(CurrentBody), boneTransforms: NativeBodyShape.Outfit(CurrentBody, chestSize, hem), headLocalSkinning: false,
                metadataOverride: generated.Metadata, geometryOverride: generated.Geometry);
            pendingFbxPreview.transform.SetParent(modelParent, false);
            foreach (var renderer in pendingFbxPreview.GetComponentsInChildren<Renderer>()) renderer.sharedMaterial.SetColor("_SkinColor", SelectedSkinColor);
            if (uniformModel != null) uniformModel.SetActive(false);
            SetNativeAccessoryVisibility(false);
            var renderers = pendingFbxPreview.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0) { var bounds = renderers[0].bounds; foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds); preview.FrameBounds(bounds); }
            RefreshFbxStatus();
        } catch (Exception e) { fbxStatus.text = e.Message; Debug.LogError("[UniformMaker FBX] " + e.Message, this); }
    }

    public void OnClickApplyFbx()
    {
        if (pendingFbxSettings == null) return;
        try {
            BuildFbx(FbxTemplate(pendingFbxKind), pendingFbxSettings);
            var edit = ModelEdit;
            // Select a native material template before clearing an Empty selection.
            if (PartOverride(pendingFbxKind) == 0 && pendingFbxKind != UniformPartKind.Upper && pendingFbxKind != UniformPartKind.Lower) {
                var option = catalog.OptionsFor(pendingFbxKind, CurrentBody).FirstOrDefault();
                if (option != null) SetAccessory(pendingFbxKind, option.Id, false);
            }
            if (pendingFbxKind == UniformPartKind.Upper) edit.HideUpper = false;
            else if (pendingFbxKind == UniformPartKind.Lower) edit.HideLower = false;
            else SetAccessory(pendingFbxKind, PartOverride(pendingFbxKind), false);
            if (pendingFbxWhole) {
                edit.Fbx = pendingFbxSettings.Clone(); edit.PartFbx.Remove(UniformPartKind.Upper); edit.PartFbx.Remove(UniformPartKind.Lower);
                edit.HideUpper = edit.HideLower = false;
                edit.Skin = edit.Number = edit.Name = edit.Armband = edit.Gloves = 0;
                edit.HideSkin = edit.HideNumber = edit.HideName = edit.HideArmband = edit.HideGloves = true;
                foreach (var kind in new[] { UniformPartKind.Skin, UniformPartKind.Number, UniformPartKind.Name, UniformPartKind.Armband, UniformPartKind.Gloves }) edit.PartFbx.Remove(kind);
                ClearPartEdits(CurrentEdit, UniformPartKind.Name, UniformPartKind.Skin, UniformPartKind.Number, UniformPartKind.Armband, UniformPartKind.Gloves);
            }
            else edit.PartFbx[pendingFbxKind] = pendingFbxSettings.Clone();
            CloseFbxBindings(); RebuildBodyPreview();
        } catch (Exception e) { fbxStatus.text = e.Message; Debug.LogError("[UniformMaker FBX] " + e.Message, this); }
    }

    void ReleaseFbxPreview()
    {
        if (pendingFbxPreview != null) {
            pendingFbxPreview.SetActive(false); foreach (var resource in pendingFbxPreview.GetComponentsInChildren<RuntimeHeadResources>(true)) resource.Release(); DestroyAsset(pendingFbxPreview); pendingFbxPreview = null;
        }
        if (uniformModel != null) uniformModel.SetActive(true);
        SetNativeAccessoryVisibility(true);
    }

    public void CloseFbxBindings()
    {
        ReleaseFbxPreview(); pendingFbx = null; pendingFbxSettings = null;
        foreach (var row in fbxRows) { if (row != null) row.SetActive(false); DestroyAsset(row); } fbxRows.Clear();
        if (fbxBindingPanel != null) fbxBindingPanel.gameObject.SetActive(false);
        if (mainPanel != null) mainPanel.gameObject.SetActive(true);
    }
}
