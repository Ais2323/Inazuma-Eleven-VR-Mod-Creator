using System;
using System.Collections.Generic;
using System.IO;
using G4Composition;
using SFB;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    public RawImage portraitUniformIcon;
    public Toggle portraitInheritCollarToggle, portraitInheritSleeveToggle;
    int PortraitCollar => CurrentEdit?.InheritPortraitCollar == true ? 0 : collar;
    int PortraitSleeve => CurrentEdit?.InheritPortraitSleeve == true ? 0 : sleeve;
    string PortraitKey => PortraitCollar + "/" + PortraitSleeve;
    public Button portraitIconUploadButton, portraitIconExportButton, portraitMaskUploadButton;
    readonly Dictionary<string, UniformPortraitIcon> portraitSources = new();
    UniformPortraitIcon currentPortraitSource;
    Texture2D portraitTexture;
    string portraitSourceKey, appliedPortraitPng, appliedMaskPng;
    Color32 portraitSkin;

    void InitializePortraitControls()
    {
        if (portraitInheritCollarToggle != null) portraitInheritCollarToggle.onValueChanged.AddListener(value => {
            if (CurrentEdit == null) return;
            CurrentEdit.InheritPortraitCollar = value; RefreshPortraitIcon();
        });
        if (portraitInheritSleeveToggle != null) portraitInheritSleeveToggle.onValueChanged.AddListener(value => {
            if (CurrentEdit == null) return;
            CurrentEdit.InheritPortraitSleeve = value; RefreshPortraitIcon();
        });
        if (portraitUniformIcon != null) portraitUniformIcon.enabled = false;
        if (portraitMaskUploadButton != null) {
            portraitMaskUploadButton.interactable = false;
            portraitMaskUploadButton.onClick.AddListener(OnClickUploadUniformPortraitMask);
        }
        if (portraitIconUploadButton != null) portraitIconUploadButton.interactable = false;
        if (portraitIconExportButton != null) portraitIconExportButton.interactable = false;
        if (portraitIconUploadButton != null) portraitIconUploadButton.onClick.AddListener(OnClickUploadUniformPortrait);
        if (portraitIconExportButton != null) portraitIconExportButton.onClick.AddListener(OnClickExportUniformPortrait);
    }

    void RefreshPortraitIcon()
    {
        if (portraitUniformIcon == null || catalog == null || selected < 0 || CurrentBody == null) return;
        if (portraitInheritCollarToggle != null) portraitInheritCollarToggle.SetIsOnWithoutNotify(CurrentEdit.InheritPortraitCollar);
        if (portraitInheritSleeveToggle != null) portraitInheritSleeveToggle.SetIsOnWithoutNotify(CurrentEdit.InheritPortraitSleeve);
        var outfit = catalog.Resolve(catalog.Entries[selected], side, role, CurrentBody, PortraitSleeve, 0, CurrentEdit.Clothes);
        string stem = UniformPortraitIcon.Find(outfit, CurrentBody, PortraitCollar, PortraitSleeve);
        if (stem == null) {
            var fallback = catalog.Resolve(catalog.Entries[selected], side, role, CurrentBody, PortraitSleeve, 0);
            stem = UniformPortraitIcon.Find(fallback, CurrentBody, PortraitCollar, PortraitSleeve);
        }
        string png = null;
        CurrentEdit?.Portraits.TryGetValue(PortraitKey, out png);
        string mask = null;
        CurrentEdit?.PortraitMasks.TryGetValue(PortraitKey, out mask);
        Color32 skin = SelectedSkinColor;
        if (portraitSourceKey == stem && ReferenceEquals(appliedPortraitPng, png) && ReferenceEquals(appliedMaskPng, mask) && skin.Equals(portraitSkin)) return;
        portraitSourceKey = stem; appliedPortraitPng = png; appliedMaskPng = mask; portraitSkin = skin;
        DestroyAsset(portraitTexture); portraitTexture = null; currentPortraitSource = null;
        portraitUniformIcon.texture = null;
        try {
            if (stem != null) {
                if (!portraitSources.TryGetValue(stem, out currentPortraitSource)) {
                    currentPortraitSource = UniformPortraitIcon.Load(stem); portraitSources.Add(stem, currentPortraitSource);
                }
                var image = currentPortraitSource;
                portraitTexture = new Texture2D(image.Width, image.Height, TextureFormat.RGBA32, false);
                portraitTexture.SetPixels32(image.Composite(png, skin, mask)); portraitTexture.Apply();
                portraitUniformIcon.texture = portraitTexture; portraitUniformIcon.uvRect = new Rect(0, 1, 1, -1);
            }
        }
        catch (Exception error) { Debug.LogWarning("[Uniform portrait] " + error.Message, this); }
        portraitUniformIcon.enabled = portraitTexture != null;
        if (portraitMaskUploadButton != null) portraitMaskUploadButton.interactable = currentPortraitSource != null;
        if (portraitIconUploadButton != null) portraitIconUploadButton.interactable = currentPortraitSource != null;
        if (portraitIconExportButton != null) portraitIconExportButton.interactable = currentPortraitSource != null;
    }

    public void OnClickUploadUniformPortrait()
    {
        if (CurrentEdit == null || currentPortraitSource == null) return;
        var files = StandaloneFileBrowser.OpenFilePanel(LocalizationManager.GetText("UniformUploadPortraitIcon"), "",
            new[] { new ExtensionFilter("Image", "png", "jpg", "jpeg") }, false);
        if (files == null || files.Length == 0 || string.IsNullOrWhiteSpace(files[0])) return;
        try { SetUniformPortrait(File.ReadAllBytes(files[0])); }
        catch (Exception error) { Debug.LogError("[Uniform portrait] " + error.Message, this); }
    }

    public void OnClickUploadUniformPortraitMask()
    {
        if (CurrentEdit == null || currentPortraitSource == null) return;
        var files = StandaloneFileBrowser.OpenFilePanel(LocalizationManager.GetText("UniformUploadPortraitMask"), "",
            new[] { new ExtensionFilter("Image", "png", "jpg", "jpeg") }, false);
        if (files == null || files.Length == 0 || string.IsNullOrWhiteSpace(files[0])) return;
        try { SetUniformPortraitMask(File.ReadAllBytes(files[0])); }
        catch (Exception error) { Debug.LogError("[Uniform portrait mask] " + error.Message, this); }
    }

    internal void SetUniformPortraitMask(byte[] bytes)
    {
        if (CurrentEdit == null || currentPortraitSource == null) throw new InvalidOperationException("Select a uniform portrait first.");
        CurrentEdit.PortraitMasks[PortraitKey] = Convert.ToBase64String(UniformPortraitIcon.NormalizeImage(bytes));
        RefreshPortraitIcon();
    }

    internal void SetUniformPortrait(byte[] bytes)
    {
        if (CurrentEdit == null || currentPortraitSource == null) throw new InvalidOperationException("Select a uniform portrait first.");
        string png = Convert.ToBase64String(UniformPortraitIcon.NormalizeImage(bytes));
        CurrentEdit.Portraits[PortraitKey] = png;
        RefreshPortraitIcon();
    }

    public void OnClickExportUniformPortrait()
    {
        if (currentPortraitSource == null) return;
        var folders = StandaloneFileBrowser.OpenFolderPanel(LocalizationManager.GetText("UniformExportPortraitIcon"),
            SettingManager.GetExportDirectory("textureExportDirectory"), false);
        if (folders == null || folders.Length == 0 || string.IsNullOrWhiteSpace(folders[0])) return;
        SettingManager.RememberExportDirectory("textureExportDirectory", folders[0]);
        try {
            string directory = Path.Combine(folders[0], catalog.Entries[selected].Key + "_" + side + "_" + role + "_" + CurrentBody.Int(4) + "_portrait");
            WriteUniformPortrait(directory);
            Debug.Log("[Uniform portrait] Exported " + directory, this);
        }
        catch (Exception error) { Debug.LogError("[Uniform portrait] " + error.Message, this); }
    }

    internal void WriteUniformPortrait(string directory)
    {
        var image = currentPortraitSource ?? throw new InvalidOperationException("No uniform portrait is available.");
        CurrentEdit.Portraits.TryGetValue(PortraitKey, out var png);
        CurrentEdit.PortraitMasks.TryGetValue(PortraitKey, out var mask);
        var maskPixels = string.IsNullOrEmpty(mask) ? image.MaskPixels : UniformPortraitIcon.ReadPngPixels(mask, image.Width, image.Height);
        var clothPixels = string.IsNullOrEmpty(png) ? image.ClothPixels : UniformPortraitIcon.ReadPngPixels(png, image.Width, image.Height);
        var files = new Dictionary<string, byte[]> {
            { "shirt.png", UniformPortraitIcon.EncodePng(clothPixels, image.Width, image.Height) },
            { "skin_mask.png", UniformPortraitIcon.EncodePng(maskPixels, image.Width, image.Height) },
            { "preview.png", UniformPortraitIcon.EncodePng(image.Composite(png, SelectedSkinColor, mask), image.Width, image.Height) },
            { image.Stem + "_1.dds", image.EncodeCloth(png) }, { image.Stem + "_2.dds", image.EncodeMask(mask) }
        };
        Directory.CreateDirectory(directory);
        foreach (var file in files) File.WriteAllBytes(Path.Combine(directory, file.Key), file.Value);
    }

    void ReleasePortraitIcon()
    {
        if (portraitUniformIcon != null) portraitUniformIcon.texture = null;
        DestroyAsset(portraitTexture); portraitTexture = null; portraitSources.Clear();
    }
}
