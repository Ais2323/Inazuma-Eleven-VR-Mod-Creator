using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;
using SFB;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    public Transform partsPanel, mainPanel, modelSelectPanel, modelButtons;
    public Button modelPrevious, modelNext, modelClose;
    public InputField modelPageInput;

    sealed class TextureEdit
    {
        internal string OriginalName, Path;
        byte[] bytes;
        string base64;
        Texture2D texture;
        int references = 1;
        internal int Width, Height;
        internal bool Linear;
        internal byte[] SourceBytes { get => bytes ??= Convert.FromBase64String(base64); set { bytes = value; base64 = null; } }
        internal string SourceBase64 { get => base64 ??= Convert.ToBase64String(bytes); set { base64 = value; bytes = null; } }
        internal int MipCount;
        internal Texture2D LoadedTexture => texture;
        internal Texture2D Texture {
            get {
                if (texture == null) {
                    texture = UniformTextureCodec.Decode(SourceBytes, System.IO.Path.GetExtension(Path), Width, Height, Linear);
                    texture.name = OriginalName;
                }
                return texture;
            }
            set { texture = value; if (value != null) { Width = value.width; Height = value.height; } }
        }
        internal TextureEdit Copy() { references++; return this; }
        internal void Release() { if (--references == 0) DestroyAsset(texture); }
    }
    sealed class KitEdit
    {
        internal uint Clothes, Armband, Gloves;
        internal uint Skin, Number, Name;
        internal bool HideSkin, HideNumber, HideArmband, HideGloves, HideName;
        internal UniformFbxModel Fbx;
        internal readonly Dictionary<string, TextureEdit> Textures = new();
        internal readonly Dictionary<string, string> Portraits = new();
    }
    sealed class PartControls
    {
        internal Transform Root;
        internal Button Select, Export;
        internal RawImage Icon;
        internal readonly Button[] Buttons = new Button[4];
        internal readonly RawImage[] Images = new RawImage[4];
        internal readonly List<GameObject> ExtraRows = new();
    }
    static readonly string[] TextureProperties = { "_MainTex", "_OcTex", "_MskTex", "_SpmTex" };
    static readonly string[] TextureControls = { "BaseTex", "OcTex", "SkinTex", "SpmTex" };
    static string TextureKey(UniformPartKind kind, int channel, string name) => kind + "/" + channel + "/" + name;
    readonly Dictionary<string, KitEdit> kitEdits = new();
    readonly Dictionary<UniformPartKind, PartControls> partControls = new();
    readonly List<Button> modelChoices = new();
    readonly List<Sprite> modelSprites = new();
    readonly Dictionary<UniformPartKind, Texture2D> partIcons = new();
    GameObject uniformModel, armbandModel, gloveModel, skinModel, nameModel;
    UniformPartKind modelKind;
    int modelPage;
    Coroutine modelPageRoutine;
    List<UniformCatalog.ModelOption> modelOptions;

    KitEdit CurrentEdit
    {
        get
        {
            if (catalog == null || selected < 0) return null;
            return EditFor(side, role, true);
        }
    }

    KitEdit EditFor(int wantedSide, int wantedRole, bool create)
    {
        if (CurrentBody == null || selected < 0) return null;
        string prefix = catalog.Entries[selected].Id + "/" + wantedSide + "/" + wantedRole + "/";
        if (kitEdits.TryGetValue(prefix + CurrentBody.Int(4), out var edit)) return edit;
        kitEdits.TryGetValue(prefix + "-1", out var legacy);
        if (!create) return legacy;
        edit = new KitEdit { Clothes = legacy?.Clothes ?? 0, Armband = legacy?.Armband ?? 0, Gloves = legacy?.Gloves ?? 0 };
        edit.Fbx = legacy?.Fbx?.Clone();
        edit.Name = legacy?.Name ?? 0; edit.HideName = legacy?.HideName ?? false;
        edit.Skin = legacy?.Skin ?? 0; edit.Number = legacy?.Number ?? 0;
        edit.HideSkin = legacy?.HideSkin ?? false; edit.HideNumber = legacy?.HideNumber ?? false;
        edit.HideArmband = legacy?.HideArmband ?? false; edit.HideGloves = legacy?.HideGloves ?? false;
        if (legacy != null) foreach (var pair in legacy.Portraits) edit.Portraits.Add(pair.Key, pair.Value);
        if (legacy != null) foreach (var pair in legacy.Textures) {
            edit.Textures.Add(pair.Key, pair.Value.Copy());
        }
        kitEdits.Add(prefix + CurrentBody.Int(4), edit); return edit;
    }

    void InitializePartControls()
    {
        if (partsPanel == null || modelSelectPanel == null) return;
        foreach (UniformPartKind kind in Enum.GetValues(typeof(UniformPartKind)))
        {
            var root = partsPanel.Find(kind.ToString());
            if (root == null) continue;
            var controls = new PartControls { Root = root, Select = root.Find("Select")?.GetComponent<Button>(),
                Icon = root.Find("Select/RawImage")?.GetComponent<RawImage>() };
            partControls.Add(kind, controls);
            EnsureSpmControl(root);
            if (controls.Select != null) controls.Select.onClick.AddListener(() => OpenModelSelect(kind));
            controls.Export = partsPanel.Find(kind + "Export")?.GetComponent<Button>();
            if (controls.Export != null) controls.Export.onClick.AddListener(() => ExportPart(kind));
            for (int channel = 0; channel < TextureControls.Length; channel++)
            {
                int captured = channel;
                var button = root.Find(TextureControls[channel])?.GetComponent<Button>();
                controls.Buttons[channel] = button;
                controls.Images[channel] = root.Find(TextureControls[channel] + "/RawImage")?.GetComponent<RawImage>();
                if (button != null) button.onClick.AddListener(() => ImportPartTexture(kind, captured));
            }
        }
        foreach (Transform child in modelButtons)
        {
            var button = child.GetComponent<Button>();
            if (button == null) continue;
            int slot = modelChoices.Count;
            button.onClick.AddListener(() => SelectModelSlot(slot));
            modelChoices.Add(button);
        }
        modelPrevious.onClick.AddListener(() => SetModelPage(modelPage - 1));
        modelNext.onClick.AddListener(() => SetModelPage(modelPage + 1));
        modelClose.onClick.AddListener(CloseModelSelect);
        modelPageInput.onEndEdit.AddListener(value => { if (int.TryParse(value, out int page)) SetModelPage(page - 1); });
    }

    public void OpenModelSelect(UniformPartKind kind)
    {
        if (catalog == null || selected < 0 || modelChoices.Count == 0 || kind == UniformPartKind.Gloves && role != 1) return;
        if (kind != UniformPartKind.Upper && kind != UniformPartKind.Armband && kind != UniformPartKind.Gloves && kind != UniformPartKind.Skin && kind != UniformPartKind.Number && kind != UniformPartKind.Name) return;
        CloseFbxBindings();
        modelKind = kind;
        modelOptions = catalog.OptionsFor(kind, CurrentBody);
        if (kind != UniformPartKind.Upper) {
            modelOptions.Insert(0, new UniformCatalog.ModelOption { Name = "Empty", Empty = true });
            modelOptions.Insert(1, new UniformCatalog.ModelOption { Name = "Inherit", Inherit = true });
        }
        mainPanel.gameObject.SetActive(false);
        modelSelectPanel.gameObject.SetActive(true);
        SetModelPage(0);
    }

    public void CloseModelSelect()
    {
        if (modelSelectPanel == null) return;
        if (modelPageRoutine != null) { StopCoroutine(modelPageRoutine); modelPageRoutine = null; }
        ReleaseModelSprites();
        modelSelectPanel.gameObject.SetActive(false);
        if (mainPanel != null) mainPanel.gameObject.SetActive(true);
    }

    void SetModelPage(int page)
    {
        if (modelOptions == null) return;
        modelPage = Mathf.Clamp(page, 0, Mathf.Max(0, (modelOptions.Count - 1) / modelChoices.Count));
        if (modelPageRoutine != null) StopCoroutine(modelPageRoutine);
        modelPageRoutine = StartCoroutine(FillModelPage());
    }

    IEnumerator FillModelPage()
    {
        ReleaseModelSprites();
        int start = modelPage * modelChoices.Count;
        modelPrevious.interactable = modelPage > 0;
        modelNext.interactable = start + modelChoices.Count < modelOptions.Count;
        modelPageInput.SetTextWithoutNotify((modelPage + 1).ToString());
        foreach (var button in modelChoices) { button.interactable = false; SetModelImage(button, null); }
        for (int slot = 0; slot < modelChoices.Count; slot++)
        {
            int index = start + slot;
            var button = modelChoices[slot];
            button.gameObject.SetActive(index < modelOptions.Count);
            if (index >= modelOptions.Count) continue;
            var option = modelOptions[index];
            button.name = option.Name;
            SetOptionLabel(button, null);
            if (option.Empty || option.Inherit) {
                SetModelImage(button, null); SetOptionLabel(button, LocalizationManager.GetText(option.Empty ? "UniformPartEmpty" : "UniformPartInherit"));
                button.interactable = option.Empty ? !PartHidden(modelKind) : PartHidden(modelKind) || PartOverride(modelKind) != 0;
                continue;
            }
            GameObject sample = null;
            try
            {
                sample = CreateModelSample(modelKind, option);
                if (sample != null)
                {
                    var texture = RenderModelThumbnail(sample);
                    var sprite = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(.5f, .5f));
                    modelSprites.Add(sprite);
                    SetModelImage(button, sprite);
                    uint current = PartOverride(modelKind);
                    if (current == 0 && modelKind == UniformPartKind.Upper) current = GetSelection(side, role).ModelId;
                    button.interactable = PartHidden(modelKind) || !catalog.MatchesModel(modelKind, option, current);
                }
            }
            catch (Exception error) { Debug.LogWarning("[UniformMaker] " + option.Name + ": " + error.Message, this); }
            finally { ReleaseSample(sample); }
            yield return null;
        }
        modelPageRoutine = null;
    }

    static void SetModelImage(Button button, Sprite sprite)
    {
        if (button == null) return;
        var image = button.transform.Find("Image")?.GetComponent<Image>();
        if (image != null) { image.sprite = sprite; image.preserveAspect = true; image.enabled = sprite != null; }
    }

    void SelectModelSlot(int slot)
    {
        int index = modelPage * modelChoices.Count + slot;
        if (modelOptions == null || index >= modelOptions.Count || !modelChoices[slot].interactable) return;
        var option = modelOptions[index];
        if (option.Empty || option.Inherit) SetAccessory(modelKind, 0, option.Empty);
        else SelectPartModel(modelKind, option.Id);
        CloseModelSelect();
    }

    internal void SelectPartModel(UniformPartKind kind, uint id)
    {
        var edit = CurrentEdit;
        if (edit == null) return;
        if (kind == UniformPartKind.Upper)
        {
            var resolved = NativeUniformFashion.Resolve(id, CurrentBody, 0, 0);
            if (resolved == null) return;
            edit.Clothes = resolved.ModelId;
            edit.Fbx = null;
            ClearPartEdits(edit, UniformPartKind.Upper, UniformPartKind.Lower, UniformPartKind.Number);
        }
        else if (kind == UniformPartKind.Armband || kind == UniformPartKind.Skin || kind == UniformPartKind.Number || kind == UniformPartKind.Name || kind == UniformPartKind.Gloves && role == 1)
        { SetAccessory(kind, id, false); return; }
        else return;
        RebuildBodyPreview();
    }

    NativeUniformFashion.Selection ResolveArmband(NativeUniformFashion.Selection outfit) => ResolveAccessory(UniformPartKind.Armband, outfit);

    IEnumerable<Renderer> PartRenderers(UniformPartKind kind)
    {
        GameObject root = kind == UniformPartKind.Name ? nameModel : kind == UniformPartKind.Skin ? skinModel : kind == UniformPartKind.Number ? numberModel : kind == UniformPartKind.Armband ? armbandModel :
            kind == UniformPartKind.Gloves ? gloveModel : uniformModel;
        if (root == null) return Enumerable.Empty<Renderer>();
        var renderers = root.GetComponentsInChildren<Renderer>(true).AsEnumerable();
        if (kind == UniformPartKind.Upper || kind == UniformPartKind.Lower)
            renderers = renderers.Where(r => (r.name.Contains("_30") || r.sharedMaterial.name.EndsWith("_30M", StringComparison.OrdinalIgnoreCase)) == (kind == UniformPartKind.Lower));
        if (kind == UniformPartKind.Number || kind == UniformPartKind.Name) renderers = renderers.Where(r => UniformLabelAsset.IsName(r.name) == (kind == UniformPartKind.Name));
        return renderers;
    }

    Texture CurrentTexture(UniformPartKind kind, int channel, string name = null) => PartRenderers(kind)
        .Select(r => r.sharedMaterial.HasProperty(TextureProperties[channel]) ? r.sharedMaterial.GetTexture(TextureProperties[channel]) : null).FirstOrDefault(t => EditableTexture(t) && (name == null || t.name == name));

    void RefreshPartControls()
    {
        if (fbxImportButton != null) fbxImportButton.interactable = selected >= 0;
        foreach (var pair in partControls)
        {
            var kind = pair.Key; var controls = pair.Value;
            controls.Root.gameObject.SetActive(kind != UniformPartKind.Gloves || role == 1);
            if (controls.Export != null)
            {
                controls.Export.gameObject.SetActive(kind != UniformPartKind.Gloves || role == 1);
                controls.Export.interactable = CurrentTexture(kind, 0) != null;
            }
            if (controls.Select != null) controls.Select.interactable = selected >= 0 && (kind != UniformPartKind.Gloves || role == 1);
            if (controls.Icon != null)
            {
                GameObject root = kind == UniformPartKind.Upper ? uniformModel : kind == UniformPartKind.Name ? nameModel : kind == UniformPartKind.Skin ? skinModel : kind == UniformPartKind.Number ? numberModel : kind == UniformPartKind.Armband ? armbandModel : kind == UniformPartKind.Gloves ? gloveModel : null;
                if (root != null && !partIcons.ContainsKey(kind)) partIcons[kind] = RenderModelThumbnail(root);
                controls.Icon.texture = partIcons.TryGetValue(kind, out var icon) ? icon : null;
                controls.Icon.uvRect = new Rect(0, 0, 1, 1);
                controls.Icon.enabled = controls.Icon.texture != null;
            }
            RefreshMaterialRows(kind, controls);
        }
    }

    static void SetTextureImage(RawImage image, Texture texture)
    {
        if (image == null) return;
        image.texture = texture;
        image.uvRect = new Rect(0, 1, 1, -1);
        image.enabled = texture != null;
    }

    void ClearPartImages()
    {
        foreach (var icon in partIcons.Values) DestroyAsset(icon);
        partIcons.Clear();
        foreach (var controls in partControls.Values)
        {
            ClearMaterialRows(controls);
            SetTextureImage(controls.Icon, null);
            foreach (var image in controls.Images) SetTextureImage(image, null);
        }
    }

    void ImportPartTexture(UniformPartKind kind, int channel, string name = null)
    {
        if (CurrentTexture(kind, channel, name) == null) return;
        var paths = StandaloneFileBrowser.OpenFilePanel("Select DDS or PNG", "", new[] { new ExtensionFilter("Texture", "dds", "png") }, false);
        if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;
        try { LoadPartTexture(kind, channel, paths[0], name); }
        catch (Exception error) { Debug.LogError("[UniformMaker] " + error.Message, this); }
    }

    internal void LoadPartTexture(UniformPartKind kind, int channel, string path, string name = null)
    {
        var original = CurrentTexture(kind, channel, name);
        if (original == null || CurrentEdit == null) throw new InvalidOperationException("No texture selected.");
        byte[] bytes = File.ReadAllBytes(path);
        Texture2D texture = null;
        try
        {
            texture = UniformTextureCodec.Decode(bytes, System.IO.Path.GetExtension(path), original.width, original.height, channel != 0);
            texture.name = original.name; texture.wrapMode = TextureWrapMode.Repeat; texture.filterMode = FilterMode.Bilinear;
            string key = TextureKey(kind, channel, original.name);
            CurrentEdit.Textures.TryGetValue(key, out var old);
            CurrentEdit.Textures[key] = new TextureEdit { OriginalName = original.name, Texture = texture, Path = path, SourceBytes = bytes, Linear = channel != 0, MipCount = old != null ? old.MipCount : ((Texture2D)original).mipmapCount };
            texture = null;
            ApplyPartTextures();
            old?.Release();
            if (partIcons.TryGetValue(kind, out var icon)) { DestroyAsset(icon); partIcons.Remove(kind); }
            RefreshPartControls();
        }
        finally { if (texture != null) DestroyAsset(texture); }
    }

    void ApplyPartTextures()
    {
        if (CurrentEdit == null) return;
        foreach (var pair in CurrentEdit.Textures)
        {
            var key = pair.Key.Split('/');
            var kind = (UniformPartKind)Enum.Parse(typeof(UniformPartKind), key[0]); int channel = int.Parse(key[1]); var edit = pair.Value;
                    foreach (var renderer in PartRenderers(kind))
                    {
                        var material = renderer.sharedMaterial;
                        if (!material.HasProperty(TextureProperties[channel])) continue;
                        var original = material.GetTexture(TextureProperties[channel]);
                        if (original != null && original.name == edit.OriginalName) material.SetTexture(TextureProperties[channel], edit.Texture);
                    }
        }
    }

    static void ClearPartEdits(KitEdit edit, params UniformPartKind[] kinds)
    {
        foreach (var kind in kinds)
            foreach (var key in edit.Textures.Keys.Where(k => k.StartsWith(kind + "/", StringComparison.Ordinal)).ToArray())
            { edit.Textures[key].Release(); edit.Textures.Remove(key); }
    }

    void ReleaseModelSprites()
    {
        foreach (var button in modelChoices) SetModelImage(button, null);
        foreach (var sprite in modelSprites) { if (sprite != null) { DestroyAsset(sprite.texture); DestroyAsset(sprite); } }
        modelSprites.Clear();
    }
    void DisposePartEditor()
    {
        ReleaseModelSprites();
        foreach (var edit in kitEdits.Values) foreach (var texture in edit.Textures.Values) texture.Release();
        kitEdits.Clear();
    }
    static void DestroyAsset(UnityEngine.Object value)
    { if (value == null) return; if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
}
