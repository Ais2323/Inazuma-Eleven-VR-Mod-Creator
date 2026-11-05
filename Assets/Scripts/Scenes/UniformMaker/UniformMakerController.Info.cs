using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using G4Composition;
using G4Runtime;
using SFB;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    public Transform nameLocaleButtons;
    public Button iconButton, nameAutoTranslateButton;
    public RawImage uniformIcon;
    readonly List<UniformLocalizedName> uniformNames = new();
    string nameLocale;
    byte[] uploadedIcon;
    Texture2D infoIconTexture;
    CancellationTokenSource infoTranslation;
    static string NameLocale(string value) => value == "jp" ? "ja" : value == "zhcn" || value == "zh-cn" ? "zh_hans" :
        value == "zhtw" || value == "zh-tw" ? "zh_hant" : value;

    void InitializeInfo()
    {
        nameLocale = NameLocale(LocalizationManager.NowLoc);
        if (!CharacterIdentityCatalog.Locales.Contains(nameLocale)) nameLocale = "en";
        if (nameLocaleButtons != null)
            foreach (Transform child in nameLocaleButtons)
            {
                var button = child.GetComponent<Button>();
                if (button == null) continue;
                string language = NameLocale(child.name);
                button.onClick.AddListener(() => { nameLocale = language; RefreshUniformInfo(); });
            }
        if (uniformNameInput != null) uniformNameInput.onValueChanged.AddListener(value => {
            var name = uniformNames.Find(n => n.locale == nameLocale);
            if (name != null) name.name = value;
        });
        if (iconButton != null) iconButton.onClick.AddListener(OnClickUniformIcon);
        if (nameAutoTranslateButton != null) nameAutoTranslateButton.onClick.AddListener(OnClickTranslateUniformName);
    }

    void LoadUniformInfo()
    {
        infoTranslation?.Cancel();
        uniformNames.Clear();
        foreach (string language in CharacterIdentityCatalog.Locales)
            uniformNames.Add(new UniformLocalizedName { locale = language, name = catalog.Name(catalog.Entries[selected], language) });
        uploadedIcon = null;
        LoadNativeIcon(); RefreshUniformInfo();
    }

    void RestoreUniformInfo(UniformMakerDocument doc)
    {
        LoadUniformInfo();
        foreach (var name in uniformNames)
            name.name = doc.names?.Find(n => n.locale == name.locale)?.name ?? doc.uniformName ?? name.name;
        if (!string.IsNullOrEmpty(doc.iconPngBase64)) SetUploadedIcon(Convert.FromBase64String(doc.iconPngBase64));
        RefreshUniformInfo();
    }

    void RefreshUniformInfo()
    {
        if (uniformNameInput != null) uniformNameInput.SetTextWithoutNotify(uniformNames.Find(n => n.locale == nameLocale)?.name ?? "");
        if (nameLocaleButtons != null)
            foreach (Transform child in nameLocaleButtons)
            {
                var button = child.GetComponent<Button>();
                if (button != null) button.interactable = NameLocale(child.name) != nameLocale;
            }
        if (iconButton != null) iconButton.interactable = selected >= 0;
        if (nameAutoTranslateButton != null) nameAutoTranslateButton.interactable = selected >= 0 && infoTranslation == null;
    }

    void ReleaseInfoIcon()
    {
        if (uniformIcon != null) uniformIcon.texture = null;
        DestroyAsset(infoIconTexture); infoIconTexture = null;
    }

    void LoadNativeIcon()
    {
        ReleaseInfoIcon();
        try
        {
            string path = Path.Combine(DumpDataManager.Dx11Path, UniformInventoryIcon.RelativePath);
            var raw = RawTexture.Read(File.ReadAllBytes(path), path)
                .FirstOrDefault(t => t.Name == catalog.Entries[selected].Key);
            if (raw == null) throw new InvalidDataException("Missing uniform icon: " + catalog.Entries[selected].Key);
            var data = G4TextureParser.ParseDDS(raw.Dds, raw.Name);
            infoIconTexture = new Texture2D(data.Width, data.Height, data.Format, data.MipCount, false);
            infoIconTexture.LoadRawTextureData(data.Pixels); infoIconTexture.Apply(false, true);
            if (uniformIcon != null) { uniformIcon.texture = infoIconTexture; uniformIcon.uvRect = new Rect(0, 1, 1, -1); }
        }
        catch (Exception e) { Debug.LogWarning("[UniformMaker] " + e.Message, this); }
    }

    public void OnClickUniformIcon()
    {
        if (selected < 0) return;
        var files = StandaloneFileBrowser.OpenFilePanel("Uniform icon", "", new[] { new ExtensionFilter("Image", "png", "jpg", "jpeg") }, false);
        if (files == null || files.Length == 0 || string.IsNullOrWhiteSpace(files[0])) return;
        try { SetUploadedIcon(File.ReadAllBytes(files[0])); }
        catch (Exception e) { Debug.LogError("[UniformMaker] " + e.Message, this); }
    }

    internal void SetUploadedIcon(byte[] bytes)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try
        {
            if (!texture.LoadImage(bytes) || texture.width > 8192 || texture.height > 8192) throw new InvalidDataException("Invalid icon image (maximum 8192x8192).");
            byte[] png = texture.EncodeToPNG();
            ReleaseInfoIcon(); infoIconTexture = texture; uploadedIcon = png;
            if (uniformIcon != null) { uniformIcon.texture = texture; uniformIcon.uvRect = new Rect(0, 0, 1, 1); }
        }
        catch { DestroyAsset(texture); throw; }
    }

    public async void OnClickTranslateUniformName()
    {
        string source = nameLocale;
        string text = uniformNames.Find(n => n.locale == source)?.name;
        if (infoTranslation != null || string.IsNullOrWhiteSpace(text)) return;
        var cancellation = infoTranslation = new CancellationTokenSource();
        RefreshUniformInfo();
        try
        {
            var result = await AutoTranslationService.TranslateAsync(new Dictionary<string, string> { { "name", text } }, source,
                uniformNames.Select(n => n.locale), cancellation.Token, message => Debug.Log("[AutoTranslate][Uniform] " + message));
            if (this == null || cancellation.IsCancellationRequested) return;
            foreach (var name in uniformNames)
                if (result.TryGetValue(name.locale, out var fields) && fields.TryGetValue("name", out string value)) name.name = value;
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (this != null) Debug.LogError(LocalizationManager.GetText("AutoTranslateFailed") + ": " + e.Message); }
        finally
        {
            if (ReferenceEquals(infoTranslation, cancellation)) infoTranslation = null;
            cancellation.Dispose(); if (this != null) RefreshUniformInfo();
        }
    }
}
