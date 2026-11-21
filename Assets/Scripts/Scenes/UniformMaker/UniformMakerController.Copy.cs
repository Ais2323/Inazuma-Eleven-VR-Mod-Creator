using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    [SerializeField] Transform copyPanel;
    [SerializeField] Button copyOpenButton;
    [Flags] internal enum CopyContent { Models = 1, Textures = 2, Portraits = 4 }
    internal readonly struct CopyTarget
    {
        internal readonly int Gender, Variant, Body;
        internal CopyTarget(int gender, int variant, int body) { Gender = gender; Variant = variant; Body = body; }
    }
    readonly List<(CopyTarget Target, Toggle Toggle)> copyTargets = new();
    readonly List<(Toggle Toggle, Func<CopyTarget, bool> Match)> copyGroups = new();
    Toggle copyModels, copyTextures, copyPortraits;
    Button copyApply;
    Text copySource;
    bool copying;
    CfgTable.Row copyContextBody;
    readonly Dictionary<string, List<RawTexture>> copyTextureCache = new();
    readonly Dictionary<byte[], string> copyPayloadCache = new();

    void InitializeCopyPanel()
    {
        if (copyPanel == null) return;
        copyModels = copyPanel.Find("Options/Models").GetComponent<Toggle>();
        copyTextures = copyPanel.Find("Options/Textures").GetComponent<Toggle>();
        copyPortraits = copyPanel.Find("Options/Portraits").GetComponent<Toggle>();
        copyApply = copyPanel.Find("Footer/Apply").GetComponent<Button>();
        copySource = copyPanel.Find("Source").GetComponent<Text>();
        copyOpenButton.onClick.AddListener(OnClickOpenCopy);
        copyPanel.Find("Footer/Close").GetComponent<Button>().onClick.AddListener(CloseCopyPanel);
        copyApply.onClick.AddListener(OnClickCopyApply);
        foreach (var option in new[] { copyModels, copyTextures, copyPortraits }) option.onValueChanged.AddListener(_ => RefreshCopyChecks());
        AddCopyGroup(copyPanel.Find("AllTargets").GetComponent<Toggle>(), _ => true);
        var content = copyPanel.Find("Scroll/Viewport/Targets");
        for (int g = 1; g <= 2; g++) {
            int sex = g;
            var group = content.Find("Gender" + g);
            AddCopyGroup(group.Find("All").GetComponent<Toggle>(), t => t.Gender == sex);
            for (int v = 0; v < 4; v++) {
                int variant = v;
                var row = group.Find("Variant" + v);
                AddCopyGroup(row.Find("All").GetComponent<Toggle>(), t => t.Gender == sex && t.Variant == variant);
                for (int b = 1; b <= 8; b++) {
                    var toggle = row.Find("Bodies/Body" + b).GetComponent<Toggle>();
                    copyTargets.Add((new CopyTarget(g, v, b), toggle));
                    toggle.onValueChanged.AddListener(_ => RefreshCopyChecks());
                }
            }
        }
        copyPanel.gameObject.SetActive(false);
    }

    void AddCopyGroup(Toggle toggle, Func<CopyTarget, bool> match)
    {
        copyGroups.Add((toggle, match));
        toggle.onValueChanged.AddListener(value => {
            foreach (var leaf in copyTargets.Where(t => t.Toggle.interactable && match(t.Target))) leaf.Toggle.SetIsOnWithoutNotify(value);
            RefreshCopyChecks();
        });
    }

    public void OnClickOpenCopy()
    {
        if (copying || catalog == null || selected < 0 || CurrentBody == null || copyPanel == null) return;
        UpdateNameColor();
        CloseFbxBindings(); CloseModelSelect();
        RefreshCopySource();
        foreach (var leaf in copyTargets) {
            leaf.Toggle.SetIsOnWithoutNotify(false);
            leaf.Toggle.interactable = catalog.BodyForType(leaf.Target.Body, leaf.Target.Gender) != null &&
                !(leaf.Target.Gender == gender && leaf.Target.Variant == side * 2 + role && leaf.Target.Body == bodyType);
        }
        copyModels.SetIsOnWithoutNotify(true); copyTextures.SetIsOnWithoutNotify(true); copyPortraits.SetIsOnWithoutNotify(true);
        copyPanel.gameObject.SetActive(true); copyPanel.SetAsLastSibling();
        RefreshCopyChecks();
    }

    void RefreshCopySource()
    {
        if (copySource == null) return;
        copySource.text = string.Format(LocalizationManager.GetText("UniformCopySource"),
            LocalizationManager.GetText(gender == 1 ? "UniformCopyMale" : "UniformCopyFemale"),
            LocalizationManager.GetText("UniformCopyVariant" + (side * 2 + role)), bodyType);
    }

    public void CloseCopyPanel() { if (!copying && copyPanel != null) copyPanel.gameObject.SetActive(false); }

    void RefreshCopyChecks()
    {
        foreach (var group in copyGroups) {
            var leaves = copyTargets.Where(t => t.Toggle.interactable && group.Match(t.Target)).ToArray();
            group.Toggle.interactable = !copying && leaves.Length > 0;
            group.Toggle.SetIsOnWithoutNotify(leaves.Length > 0 && leaves.All(t => t.Toggle.isOn));
        }
        if (copyApply != null) copyApply.interactable = !copying && (copyModels.isOn || copyTextures.isOn || copyPortraits.isOn) &&
            copyTargets.Any(t => t.Toggle.interactable && t.Toggle.isOn);
    }

    void OnClickCopyApply()
    {
        if (copying) return;
        var targets = copyTargets.Where(t => t.Toggle.interactable && t.Toggle.isOn).Select(t => t.Target).ToArray();
        var parts = (copyModels.isOn ? CopyContent.Models : 0) | (copyTextures.isOn ? CopyContent.Textures : 0) | (copyPortraits.isOn ? CopyContent.Portraits : 0);
        if (targets.Length > 0 && parts != 0) StartCoroutine(Loading.Track(CopySelected(parts, targets), "LoadingCopy"));
    }

    static void ReleaseCopy(KitEdit edit) { if (edit != null) foreach (var texture in edit.Textures.Values) texture.Release(); }

    internal IEnumerator CopySelected(CopyContent content, CopyTarget[] targets)
    {
        if (copying || targets == null || targets.Length == 0 || content == 0) yield break;
        if (shareAwayModels && content.HasFlag(CopyContent.Models) && targets.Any(t => t.Variant >= 2)) {
            AppLog.Error(LocalizationManager.GetText("UniformCopySharedConflict")); yield break;
        }
        copying = true;
        if (copyApply != null) RefreshCopyChecks();
        KitEdit snapshot = null;
        var prepared = new Dictionary<string, KitEdit>();
        var errors = new List<string>();
        try {
            try { snapshot = CaptureCopySource(content); }
            catch (Exception error) { errors.Add(error.Message); }
            if (snapshot != null) for (int i = 0; i < targets.Length; i++) {
                var target = targets[i];
                Loading.UpdateProgress((i + 1) * 90 / targets.Length);
                Loading.UpdateText(LocalizationManager.GetText("LoadingCopy") + " " + CopyTargetLabel(target));
                try { PrepareCopyTarget(content, snapshot, target, prepared); }
                catch (Exception error) { errors.Add(CopyTargetLabel(target) + ": " + error.Message); }
                yield return null;
            }
            if (errors.Count > 0) { AppLog.Error(LocalizationManager.GetText("UniformCopyFailed") + "\n" + string.Join("\n", errors.Distinct().Take(16))); yield break; }
            // Nothing is committed until every destination has passed validation.
            foreach (var item in prepared) {
                if (kitEdits.TryGetValue(item.Key, out var old)) ReleaseCopy(old);
                kitEdits[item.Key] = item.Value;
            }
            int count = prepared.Count; prepared.Clear();
            selections.Clear(); RebuildBodyPreview();
            copying = false; CloseCopyPanel();
            AppLog.Result(string.Format(LocalizationManager.GetText("UniformCopyDone"), count));
        }
        finally {
            ReleaseCopy(snapshot);
            foreach (var edit in prepared.Values) ReleaseCopy(edit);
            copyTextureCache.Clear(); copyPayloadCache.Clear();
            copying = false;
            if (copyApply != null) RefreshCopyChecks();
        }
    }

    string CopyTargetLabel(CopyTarget target) => LocalizationManager.GetText(target.Gender == 1 ? "UniformCopyMale" : "UniformCopyFemale") + " / " +
        LocalizationManager.GetText("UniformCopyVariant" + target.Variant) + " / " + target.Body;

    KitEdit CaptureCopySource(CopyContent content)
    {
        UpdateNameColor();
        var source = CurrentEdit;
        var copy = CloneKitEdit(source, ModelEdit);
        try {
            // Materialize native choices: zero means "follow this target", not the source.
            var outfit = GetSelection(side, role) ?? throw new InvalidDataException("Missing source uniform.");
            if (copy.Clothes == 0) copy.Clothes = outfit.ModelId;
            foreach (UniformPartKind kind in Enum.GetValues(typeof(UniformPartKind))) {
                bool absent = PartHidden(kind) || !PartRenderers(kind).Any();
                SetCopyHidden(copy, kind, absent);
                if (!absent && PartOverride(kind) == 0) SetCopyModel(copy, kind, FbxTemplate(kind).ModelId);
            }
            if (content.HasFlag(CopyContent.Textures)) {
                ReleaseCopy(copy); copy.Textures.Clear();
                // Original DDS payloads retain native dimensions, mipmaps and colour space.
                foreach (UniformPartKind kind in Enum.GetValues(typeof(UniformPartKind))) {
                    if (kind == UniformPartKind.Name || CopyHidden(copy, kind)) continue;
                    var template = FbxTemplate(kind);
                    var path = Path.Combine(DumpDataManager.Dx11Path, "chr", Path.ChangeExtension(template.UniformTexture, ".g4tx"));
                    var native = CopyNativeTextures(path);
                    foreach (var renderer in PartRenderers(kind)) for (int channel = 0; channel < TextureProperties.Length; channel++) {
                        var material = renderer.sharedMaterial;
                        var texture = material.HasProperty(TextureProperties[channel]) ? material.GetTexture(TextureProperties[channel]) : null;
                        if (!EditableTexture(texture)) continue;
                        string key = TextureKey(kind, channel, texture.name);
                        if (copy.Textures.ContainsKey(key)) continue;
                        var existing = source.Textures.FirstOrDefault(t => t.Key.StartsWith(kind + "/" + channel + "/", StringComparison.Ordinal) &&
                            (t.Value.OriginalName == texture.name || t.Value.SharedAcrossBodies && UniformMakerTexture.TextureSuffix(t.Value.OriginalName) == UniformMakerTexture.TextureSuffix(texture.name))).Value;
                        if (existing != null) { copy.Textures.Add(key, existing.Copy()); continue; }
                        var raw = native.Single(t => t.Name == texture.name);
                        var data = G4TextureParser.ParseDDS(raw.Dds, raw.Name);
                        copy.Textures.Add(key, new TextureEdit { OriginalName = raw.Name, Path = raw.Name + ".dds", SourceBytes = raw.Dds,
                            Width = data.Width, Height = data.Height, MipCount = data.MipCount, Linear = channel != 0, SharedAcrossBodies = true });
                    }
                }
                copy.HasNameColor = true; copy.NameColor = displayedNameColor;
            }
            if (content.HasFlag(CopyContent.Portraits)) {
                copy.Portraits.Clear(); copy.PortraitMasks.Clear();
                for (int c = 0; c < 2; c++) for (int s = 0; s < 2; s++) {
                    int sc = source.InheritPortraitCollar ? 0 : c, ss = source.InheritPortraitSleeve ? 0 : s;
                    var image = CopyNativePortrait(source, sc, ss);
                    string key = sc + "/" + ss;
                    source.Portraits.TryGetValue(key, out string png); source.PortraitMasks.TryGetValue(key, out string mask);
                    copy.Portraits.Add(c + "/" + s, png ?? Convert.ToBase64String(UniformPortraitIcon.EncodePng(image.ClothPixels, image.Width, image.Height)));
                    copy.PortraitMasks.Add(c + "/" + s, mask ?? Convert.ToBase64String(UniformPortraitIcon.EncodePng(image.MaskPixels, image.Width, image.Height)));
                }
            }
            return copy;
        }
        catch { ReleaseCopy(copy); throw; }
    }

    void PrepareCopyTarget(CopyContent content, KitEdit source, CopyTarget target, Dictionary<string, KitEdit> prepared)
    {
        var body = catalog.BodyForType(target.Body, target.Gender);
        if (body == null || target.Gender < 1 || target.Gender > 2 || target.Variant < 0 || target.Variant > 3) throw new InvalidDataException("Invalid destination.");
        foreach (int attribute in NativeBodyTypes.EquivalentAttributes(body)) {
            if (target.Variant == side * 2 + role && NativeBodyTypes.EquivalentAttributes(CurrentBody).Contains(attribute)) continue;
            string key = catalog.Entries[selected].Id + "/" + (target.Variant / 2) + "/" + (target.Variant % 2) + "/" + attribute;
            if (prepared.ContainsKey(key)) continue;
            string legacy = key.Substring(0, key.LastIndexOf('/') + 1) + "-1";
            var old = kitEdits.TryGetValue(key, out var exact) ? exact :
                NativeBodyTypes.EquivalentAttributes(body).Select(a => key.Substring(0, key.LastIndexOf('/') + 1) + a).Where(kitEdits.ContainsKey).Select(k => kitEdits[k]).FirstOrDefault() ??
                (kitEdits.TryGetValue(legacy, out var fallback) ? fallback : new KitEdit());
            KitEdit appearance = null;
            try {
                if (content.HasFlag(CopyContent.Models) && (!content.HasFlag(CopyContent.Textures) || !content.HasFlag(CopyContent.Portraits)))
                    appearance = PreserveCopyAppearance(old, target, attribute, key, !content.HasFlag(CopyContent.Textures), !content.HasFlag(CopyContent.Portraits));
                var previousAppearance = appearance ?? old;
                var copy = CloneKitEdit(content.HasFlag(CopyContent.Textures) ? source : previousAppearance, content.HasFlag(CopyContent.Models) ? source : old);
                if (!content.HasFlag(CopyContent.Portraits)) {
                    copy.Portraits.Clear(); copy.PortraitMasks.Clear();
                    copy.InheritPortraitCollar = previousAppearance.InheritPortraitCollar; copy.InheritPortraitSleeve = previousAppearance.InheritPortraitSleeve;
                    foreach (var item in previousAppearance.Portraits) copy.Portraits.Add(item.Key, item.Value);
                    foreach (var item in previousAppearance.PortraitMasks) copy.PortraitMasks.Add(item.Key, item.Value);
                }
                else {
                    copy.Portraits.Clear(); copy.PortraitMasks.Clear();
                    copy.InheritPortraitCollar = source.InheritPortraitCollar; copy.InheritPortraitSleeve = source.InheritPortraitSleeve;
                    foreach (var item in source.Portraits) copy.Portraits.Add(item.Key, item.Value);
                    foreach (var item in source.PortraitMasks) copy.PortraitMasks.Add(item.Key, item.Value);
                }
                try { ValidateCopyTarget(copy, target, attribute, content, key); prepared.Add(key, copy); }
                catch (Exception error) {
                    ReleaseCopy(copy);
                    if (content.HasFlag(CopyContent.Models) && !content.HasFlag(CopyContent.Textures) && error.Message.StartsWith("Texture", StringComparison.Ordinal))
                        throw new InvalidDataException(LocalizationManager.GetText("UniformCopyPreserveTexturesFailed") + "\n" + error.Message, error);
                    if (content.HasFlag(CopyContent.Models) && !content.HasFlag(CopyContent.Portraits) && (error.StackTrace?.Contains("ReadPngPixels") ?? false))
                        throw new InvalidDataException(LocalizationManager.GetText("UniformCopyPreservePortraitFailed") + "\n" + error.Message, error);
                    throw;
                }
            }
            finally { ReleaseCopy(appearance); }
        }
    }

    void ValidateCopyTarget(KitEdit copy, CopyTarget target, int attribute, CopyContent content, string key)
    {
        int oldGender = gender, oldBody = bodyType, oldSide = side, oldRole = role;
        var oldContext = copyContextBody;
        copyContextBody = catalog.BodyByAttribute(attribute);
        bool existed = kitEdits.TryGetValue(key, out var previous);
        // Temporary context is restored before yielding; no preview/UI or stored edits are committed here.
        kitEdits[key] = copy;
        gender = target.Gender; bodyType = target.Body; side = target.Variant / 2; role = target.Variant % 2; selections.Clear();
        try {
            foreach (UniformPartKind kind in Enum.GetValues(typeof(UniformPartKind))) {
                if (PartHidden(kind) || kind == UniformPartKind.Gloves && role == 0) continue;
                var template = FbxTemplate(kind);
                string path = Path.Combine(DumpDataManager.CommonPath, "chr", template.Uniform);
                var generated = content.HasFlag(CopyContent.Models) ? BuildPartFbx(template.Uniform, kind) : null;
                var parsed = G4ModelParser.Parse(generated?.Metadata ?? File.ReadAllBytes(path), generated?.Geometry ?? File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")), path);
                if (parsed.Parts.Count == 0) throw new InvalidDataException("Empty model: " + kind);
                if (!(content.HasFlag(CopyContent.Textures) || content.HasFlag(CopyContent.Models)) || kind == UniformPartKind.Name) continue;
                string texturePath = Path.Combine(DumpDataManager.Dx11Path, "chr", Path.ChangeExtension(template.UniformTexture, ".g4tx"));
                var textures = CopyNativeTextures(texturePath);
                foreach (var item in copy.Textures.Where(t => t.Key.StartsWith(kind + "/", StringComparison.Ordinal)).ToArray()) {
                    var matches = textures.Where(t => t.Name == item.Value.OriginalName).ToArray();
                    if (matches.Length == 0) matches = textures.Where(t => UniformMakerTexture.TextureSuffix(t.Name) == UniformMakerTexture.TextureSuffix(item.Value.OriginalName) && UniformLabelAsset.IsName(t.Name) == UniformLabelAsset.IsName(item.Value.OriginalName)).ToArray();
                    if (matches.Length != 1) throw new InvalidDataException("Texture mapping: " + kind + " / " + item.Value.OriginalName);
                    var data = G4TextureParser.ParseDDS(matches[0].Dds, matches[0].Name);
                    if (data.Width != item.Value.Width || data.Height != item.Value.Height || Path.GetExtension(item.Value.Path).Equals(".dds", StringComparison.OrdinalIgnoreCase) && item.Value.MipCount != data.MipCount) throw new InvalidDataException("Texture dimensions: " + kind + " / " + item.Value.OriginalName);
                    // Rename the destination binding while sharing the immutable payload.
                    string[] fields = item.Key.Split('/');
                    var remapped = new TextureEdit { OriginalName = matches[0].Name, Path = item.Value.Path, SourceBase64 = item.Value.SourceBase64,
                        Width = data.Width, Height = data.Height, MipCount = data.MipCount, Linear = item.Value.Linear, SharedAcrossBodies = false };
                    item.Value.Release(); copy.Textures.Remove(item.Key); copy.Textures.Add(TextureKey(kind, int.Parse(fields[1]), remapped.OriginalName), remapped);
                }
            }
            if (copy.Portraits.Count > 0 && (content.HasFlag(CopyContent.Portraits) || content.HasFlag(CopyContent.Models))) for (int c = 0; c < 2; c++) for (int s = 0; s < 2; s++) {
                var image = CopyNativePortrait(copy, copy.InheritPortraitCollar ? 0 : c, copy.InheritPortraitSleeve ? 0 : s);
                UniformPortraitIcon.ReadPngPixels(copy.Portraits[c + "/" + s], image.Width, image.Height);
                UniformPortraitIcon.ReadPngPixels(copy.PortraitMasks[c + "/" + s], image.Width, image.Height);
            }
        }
        finally {
            if (existed) kitEdits[key] = previous; else kitEdits.Remove(key);
            copyContextBody = oldContext;
            gender = oldGender; bodyType = oldBody; side = oldSide; role = oldRole; selections.Clear();
        }
    }

    List<RawTexture> CopyNativeTextures(string path)
    {
        if (!copyTextureCache.TryGetValue(path, out var textures)) copyTextureCache.Add(path, textures = RawTexture.Read(File.ReadAllBytes(path), path).ToList());
        return textures;
    }

    TextureEdit CopyNativeTexture(RawTexture raw, int channel)
    {
        var data = G4TextureParser.ParseDDS(raw.Dds, raw.Name);
        if (!copyPayloadCache.TryGetValue(raw.Dds, out var payload)) copyPayloadCache.Add(raw.Dds, payload = Convert.ToBase64String(raw.Dds));
        return new TextureEdit { OriginalName = raw.Name, Path = raw.Name + ".dds", SourceBase64 = payload,
            Width = data.Width, Height = data.Height, MipCount = data.MipCount, Linear = channel != 0 };
    }

    // Changing a model also changes its native default textures/portrait. Freeze
    // any unselected appearance categories first, so they remain visibly intact.
    KitEdit PreserveCopyAppearance(KitEdit old, CopyTarget target, int attribute, string key, bool textures, bool portraits)
    {
        var result = CloneKitEdit(old, old);
        int oldGender = gender, oldBody = bodyType, oldSide = side, oldRole = role;
        var oldContext = copyContextBody;
        bool existed = kitEdits.TryGetValue(key, out var previous);
        kitEdits[key] = old; copyContextBody = catalog.BodyByAttribute(attribute);
        gender = target.Gender; bodyType = target.Body; side = target.Variant / 2; role = target.Variant % 2; selections.Clear();
        try {
            var outfit = GetSelection(side, role);
            if (outfit == null) return result;
            if (textures) {
                result.HasNameColor = true;
                result.NameColor = old.HasNameColor ? old.NameColor : UniformLabelAsset.DecodeColor(CurrentLabelColors()?.UInt(1) ?? 0x000000ff);
                foreach (UniformPartKind kind in Enum.GetValues(typeof(UniformPartKind))) {
                    if (kind == UniformPartKind.Name || PartHidden(kind) || kind == UniformPartKind.Gloves && role == 0) continue;
                    var part = kind == UniformPartKind.Armband ? ResolveArmband(outfit) : ResolveAccessory(kind, outfit);
                    if (kind == UniformPartKind.Skin && string.IsNullOrEmpty(part?.Skin) ||
                        kind == UniformPartKind.Armband && string.IsNullOrEmpty(part?.Armband) ||
                        kind == UniformPartKind.Number && string.IsNullOrEmpty(part?.Number)) continue;
                    if (kind == UniformPartKind.Gloves && catalog.ResolveGloves(PartOverride(kind) != 0 ? PartOverride(kind) : catalog.Entries[selected].Gloves[side], CurrentBody) == null) continue;
                    var template = FbxTemplate(kind);
                    string path = Path.Combine(DumpDataManager.CommonPath, "chr", template.Uniform);
                    var model = G4ModelParser.Parse(File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")), path);
                    var raw = CopyNativeTextures(Path.Combine(DumpDataManager.Dx11Path, "chr", Path.ChangeExtension(template.UniformTexture, ".g4tx")));
                    var choices = raw.Select(t => new G4TextureParser.TextureData { Name = t.Name, Hash = t.Hash }).ToList();
                    foreach (var material in model.Parts.Where(p => UniformFbxWriter.MatchesPart(model, p.SourceMeshIndex, kind)).Select(p => model.Materials[p.Material]).Distinct()) {
                        var channels = new[] { G4TextureParser.Choose(material, choices), G4TextureParser.ChooseSuffix(material, choices, "oc"), G4TextureParser.ChooseSuffix(material, choices, "msk"), G4TextureParser.ChooseSuffix(material, choices, "spm") };
                        for (int c = 0; c < channels.Length; c++) {
                            if (channels[c] == null) continue;
                            string textureKey = TextureKey(kind, c, channels[c].Name);
                            if (result.Textures.Any(t => t.Key.StartsWith(kind + "/" + c + "/", StringComparison.Ordinal) &&
                                (t.Value.OriginalName == channels[c].Name || t.Value.SharedAcrossBodies && UniformMakerTexture.TextureSuffix(t.Value.OriginalName) == UniformMakerTexture.TextureSuffix(channels[c].Name)))) continue;
                            result.Textures.Add(textureKey, CopyNativeTexture(raw.Single(t => t.Name == channels[c].Name), c));
                        }
                    }
                }
            }
            if (portraits) for (int c = 0; c < 2; c++) for (int s = 0; s < 2; s++) {
                string portraitKey = c + "/" + s;
                var image = CopyNativePortrait(old, old.InheritPortraitCollar ? 0 : c, old.InheritPortraitSleeve ? 0 : s);
                if (!result.Portraits.ContainsKey(portraitKey)) result.Portraits.Add(portraitKey, Convert.ToBase64String(UniformPortraitIcon.EncodePng(image.ClothPixels, image.Width, image.Height)));
                if (!result.PortraitMasks.ContainsKey(portraitKey)) result.PortraitMasks.Add(portraitKey, Convert.ToBase64String(UniformPortraitIcon.EncodePng(image.MaskPixels, image.Width, image.Height)));
            }
            return result;
        }
        catch { ReleaseCopy(result); throw; }
        finally {
            if (existed) kitEdits[key] = previous; else kitEdits.Remove(key);
            copyContextBody = oldContext; gender = oldGender; bodyType = oldBody; side = oldSide; role = oldRole; selections.Clear();
        }
    }

    UniformPortraitIcon CopyNativePortrait(KitEdit edit, int wantedCollar, int wantedSleeve)
    {
        // Portraits follow the current variant's image selection, independently
        // of the optional home/away geometry sharing. Missing sleeve variants
        // use their normal portrait just as the game's native lookup does.
        var entry = catalog.Entries[selected];
        foreach (var outfit in new[] {
            catalog.Resolve(entry, side, role, CurrentBody, wantedSleeve, 0, edit.Clothes),
            catalog.Resolve(entry, side, role, CurrentBody, 0, 0, edit.Clothes),
            catalog.Resolve(entry, side, role, CurrentBody, wantedSleeve, 0),
            catalog.Resolve(entry, side, role, CurrentBody) }) {
            string stem = UniformPortraitIcon.Find(outfit, CurrentBody, wantedCollar, wantedSleeve);
            if (stem != null) return UniformPortraitIcon.Load(stem);
        }
        throw new InvalidDataException("Missing portrait: " + wantedCollar + "/" + wantedSleeve);
    }

    static bool CopyHidden(KitEdit e, UniformPartKind k) => k == UniformPartKind.Upper ? e.HideUpper : k == UniformPartKind.Lower ? e.HideLower : k == UniformPartKind.Skin ? e.HideSkin : k == UniformPartKind.Number ? e.HideNumber : k == UniformPartKind.Name ? e.HideName : k == UniformPartKind.Armband ? e.HideArmband : e.HideGloves;
    static void SetCopyHidden(KitEdit e, UniformPartKind k, bool value)
    {
        switch (k) { case UniformPartKind.Upper: e.HideUpper=value; break; case UniformPartKind.Lower: e.HideLower=value; break; case UniformPartKind.Skin: e.HideSkin=value; break; case UniformPartKind.Number: e.HideNumber=value; break; case UniformPartKind.Name: e.HideName=value; break; case UniformPartKind.Armband: e.HideArmband=value; break; case UniformPartKind.Gloves: e.HideGloves=value; break; }
    }
    static void SetCopyModel(KitEdit e, UniformPartKind k, uint id)
    {
        switch (k) { case UniformPartKind.Upper: case UniformPartKind.Lower: e.Clothes=id; break; case UniformPartKind.Skin: e.Skin=id; break; case UniformPartKind.Number: e.Number=id; break; case UniformPartKind.Name: e.Name=id; break; case UniformPartKind.Armband: e.Armband=id; break; case UniformPartKind.Gloves: e.Gloves=id; break; }
    }
}
