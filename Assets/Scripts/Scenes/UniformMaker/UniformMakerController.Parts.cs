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
        internal byte[] SourceBytes;
        internal int MipCount;
        internal Texture2D Texture;
    }
    sealed class KitEdit
    {
        internal uint Clothes, Armband, Gloves;
        internal readonly Dictionary<string, TextureEdit> Textures = new();
    }
    sealed class PartControls
    {
        internal Transform Root;
        internal Button Select, Export;
        internal RawImage Icon;
        internal readonly Button[] Buttons = new Button[3];
        internal readonly RawImage[] Images = new RawImage[3];
    }
    static readonly string[] TextureProperties = { "_MainTex", "_OcTex", "_MskTex" };
    static readonly string[] TextureControls = { "BaseTex", "OcTex", "SkinTex" };
    readonly Dictionary<string, KitEdit> kitEdits = new();
    readonly Dictionary<UniformPartKind, PartControls> partControls = new();
    readonly List<Button> modelChoices = new();
    readonly List<Sprite> modelSprites = new();
    readonly Dictionary<UniformPartKind, Texture2D> partIcons = new();
    GameObject uniformModel, armbandModel, gloveModel;
    UniformPartKind modelKind;
    int modelPage;
    Coroutine modelPageRoutine;
    List<UniformCatalog.ModelOption> modelOptions;

    KitEdit CurrentEdit
    {
        get
        {
            if (catalog == null || selected < 0) return null;
            string key = catalog.Entries[selected].Id + "/" + side + "/" + role;
            if (!kitEdits.TryGetValue(key, out var edit)) kitEdits.Add(key, edit = new KitEdit());
            return edit;
        }
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
        if (kind != UniformPartKind.Upper && kind != UniformPartKind.Armband && kind != UniformPartKind.Gloves) return;
        modelKind = kind;
        modelOptions = catalog.OptionsFor(kind);
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
                    uint current = modelKind == UniformPartKind.Upper ? CurrentEdit.Clothes : modelKind == UniformPartKind.Armband ? CurrentEdit.Armband : CurrentEdit.Gloves;
                    if (current == 0) current = modelKind == UniformPartKind.Gloves ? catalog.Entries[selected].Gloves[side] : GetSelection(side, role).ModelId;
                    button.interactable = !catalog.MatchesModel(modelKind, option, current);
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
        SelectPartModel(modelKind, modelOptions[index].Id);
        CloseModelSelect();
    }

    internal void SelectPartModel(UniformPartKind kind, uint id)
    {
        var edit = CurrentEdit;
        if (edit == null) return;
        if (kind == UniformPartKind.Upper)
        {
            if (NativeUniformFashion.Resolve(id, CurrentBody, 0, 0) == null) return;
            edit.Clothes = id;
            ClearPartEdits(edit, UniformPartKind.Upper, UniformPartKind.Lower, UniformPartKind.Number);
        }
        else if (kind == UniformPartKind.Armband)
        {
            edit.Armband = id; ClearPartEdits(edit, kind);
        }
        else if (kind == UniformPartKind.Gloves && role == 1)
        {
            edit.Gloves = id; ClearPartEdits(edit, kind);
        }
        else return;
        RebuildBodyPreview();
    }

    NativeUniformFashion.Selection ResolveArmband(NativeUniformFashion.Selection outfit) => CurrentEdit?.Armband > 0
        ? NativeUniformFashion.Resolve(CurrentEdit.Armband, CurrentBody, 0, 0) : outfit;

    IEnumerable<Renderer> PartRenderers(UniformPartKind kind)
    {
        GameObject root = kind == UniformPartKind.Number ? numberModel : kind == UniformPartKind.Armband ? armbandModel :
            kind == UniformPartKind.Gloves ? gloveModel : uniformModel;
        if (root == null) return Enumerable.Empty<Renderer>();
        var renderers = root.GetComponentsInChildren<Renderer>(true).AsEnumerable();
        if (kind == UniformPartKind.Upper || kind == UniformPartKind.Lower)
            renderers = renderers.Where(r => (r.name.Contains("_30") || r.sharedMaterial.name.EndsWith("_30M", StringComparison.OrdinalIgnoreCase)) == (kind == UniformPartKind.Lower));
        return renderers;
    }

    Texture CurrentTexture(UniformPartKind kind, int channel) => PartRenderers(kind)
        .Select(r => r.sharedMaterial.GetTexture(TextureProperties[channel])).FirstOrDefault(t => t != null && t is Texture2D &&
            !string.IsNullOrEmpty(t.name) && !t.name.Equals("white", StringComparison.OrdinalIgnoreCase) && !t.name.StartsWith("Default", StringComparison.OrdinalIgnoreCase));

    void RefreshPartControls()
    {
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
                GameObject root = kind == UniformPartKind.Upper ? uniformModel : kind == UniformPartKind.Armband ? armbandModel : kind == UniformPartKind.Gloves ? gloveModel : null;
                if (root != null && !partIcons.ContainsKey(kind)) partIcons[kind] = RenderModelThumbnail(root);
                controls.Icon.texture = partIcons.TryGetValue(kind, out var icon) ? icon : null;
                controls.Icon.uvRect = new Rect(0, 0, 1, 1);
                controls.Icon.enabled = controls.Icon.texture != null;
            }
            for (int channel = 0; channel < TextureProperties.Length; channel++)
            {
                var texture = CurrentTexture(kind, channel);
                SetTextureImage(controls.Images[channel], texture);
                if (controls.Buttons[channel] != null) controls.Buttons[channel].interactable = texture != null;
            }
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
            SetTextureImage(controls.Icon, null);
            foreach (var image in controls.Images) SetTextureImage(image, null);
        }
    }

    void ImportPartTexture(UniformPartKind kind, int channel)
    {
        if (CurrentTexture(kind, channel) == null) return;
        var paths = StandaloneFileBrowser.OpenFilePanel("Select DDS or PNG", "", new[] { new ExtensionFilter("Texture", "dds", "png") }, false);
        if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;
        try { LoadPartTexture(kind, channel, paths[0]); }
        catch (Exception error) { Debug.LogError("[UniformMaker] " + error.Message, this); }
    }

    internal void LoadPartTexture(UniformPartKind kind, int channel, string path)
    {
        var original = CurrentTexture(kind, channel);
        if (original == null || CurrentEdit == null) throw new InvalidOperationException("No texture selected.");
        byte[] bytes = File.ReadAllBytes(path);
        Texture2D texture = null;
        try
        {
            texture = UniformTextureCodec.Decode(bytes, System.IO.Path.GetExtension(path), original.width, original.height, channel != 0);
            texture.name = original.name; texture.wrapMode = TextureWrapMode.Repeat; texture.filterMode = FilterMode.Bilinear;
            string key = kind + "/" + channel;
            CurrentEdit.Textures.TryGetValue(key, out var old);
            CurrentEdit.Textures[key] = new TextureEdit { OriginalName = original.name, Texture = texture, Path = path, SourceBytes = bytes, MipCount = old != null ? old.MipCount : ((Texture2D)original).mipmapCount };
            texture = null;
            ApplyPartTextures();
            if (old != null) DestroyAsset(old.Texture);
            if (partIcons.TryGetValue(kind, out var icon)) { DestroyAsset(icon); partIcons.Remove(kind); }
            RefreshPartControls();
        }
        finally { if (texture != null) DestroyAsset(texture); }
    }

    void ApplyPartTextures()
    {
        if (CurrentEdit == null) return;
        foreach (UniformPartKind kind in Enum.GetValues(typeof(UniformPartKind)))
            for (int channel = 0; channel < TextureProperties.Length; channel++)
                if (CurrentEdit.Textures.TryGetValue(kind + "/" + channel, out var edit))
                    foreach (var renderer in PartRenderers(kind))
                    {
                        var material = renderer.sharedMaterial;
                        var original = material.GetTexture(TextureProperties[channel]);
                        if (original != null && original.name == edit.OriginalName) material.SetTexture(TextureProperties[channel], edit.Texture);
                    }
    }

    static void ClearPartEdits(KitEdit edit, params UniformPartKind[] kinds)
    {
        foreach (var kind in kinds)
            foreach (var key in edit.Textures.Keys.Where(k => k.StartsWith(kind + "/", StringComparison.Ordinal)).ToArray())
            { DestroyAsset(edit.Textures[key].Texture); edit.Textures.Remove(key); }
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
        foreach (var edit in kitEdits.Values) foreach (var texture in edit.Textures.Values) DestroyAsset(texture.Texture);
        kitEdits.Clear();
    }
    static void DestroyAsset(UnityEngine.Object value)
    { if (value == null) return; if (Application.isPlaying) Destroy(value); else DestroyImmediate(value); }
}
