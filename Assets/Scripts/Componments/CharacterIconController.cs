using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Globalization;
using G4Composition;
using G4Runtime;
using UnityEngine;
using UnityEngine.UI;
using SFB;

public sealed class CharacterIconController : MonoBehaviour
{
    public CharacterManager manager;
    public RawImage front, back, shirt;

    sealed class ShirtIcon
    {
        public Texture2D Texture;
        public Color32[] Pixels, Mask;
        public float BodyScale = 1;
    }
    readonly Dictionary<string, ShirtIcon> shirts = new();
    readonly Dictionary<string, ShirtIcon> bodyShirts = new();
    bool? preparedFemale;
    RenderTexture frontTexture, backTexture;
    Action<RenderTexture, bool> captureLayer;
    Color32 appliedSkin;
    bool hasSkin, initialized;
    Vector3 originalShirtScale;
    Texture2D uploadedFront, uploadedBack;
    internal bool HasUploadedPortrait => uploadedFront != null;

    internal void Initialize(bool dumpReady)
    {
        if (initialized || !dumpReady) return;
        initialized = true;
        captureLayer = StoreLayer;
        if (shirt != null) originalShirtScale = shirt.rectTransform.localScale;
        try
        {
            string kit = CharacterInfoController.DefaultUniformStem + "_" + CharacterInfoController.DefaultUniformMember;
            foreach (string stem in new[] { kit + "_00", kit + "_01", "u090141_" + kit + "_00", "u090146_" + kit + "_00" }) {
                string path = Path.Combine(DumpDataManager.Dx11Path, "menu", "200_icon", "10_icon_chr", "uniform", stem + "_l.g4tx");
                if (File.Exists(path)) shirts.Add(stem, LoadShirt(stem));
            }
            RefreshShirt();
        }
        catch (Exception error)
        {
            ClearShirts();
            Debug.LogWarning("Cannot load character shirt icons: " + error.Message, this);
        }
    }

    void LateUpdate()
    {
        if (manager == null) return;
        RefreshShirt();
        bool ready = HasUploadedPortrait || manager.HasPortraitTarget;
        SetVisible(front, ready); SetVisible(back, ready);
        if (!ready || HasUploadedPortrait) return;
        EnsureTargets();
        manager.CapturePortraitLayers(captureLayer ??= StoreLayer);
    }

    void EnsureTargets()
    {
        var source = manager.charIconCamera.targetTexture;
        if (frontTexture != null && frontTexture.width == source.width && frontTexture.height == source.height) return;
        ReleaseTargets();
        frontTexture = CreateTarget(source, "Character Icon Front");
        backTexture = CreateTarget(source, "Character Icon Back");
        if (front != null) { front.texture = frontTexture; front.uvRect = new Rect(0, 0, 1, 1); }
        if (back != null) { back.texture = backTexture; back.uvRect = new Rect(0, 0, 1, 1); }
    }
    static RenderTexture CreateTarget(RenderTexture source, string name)
    {
        var texture = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGB32,
            source.sRGB ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear)
        { name = name, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        texture.Create();
        return texture;
    }
    void StoreLayer(RenderTexture source, bool isFront) => Graphics.Blit(source, isFront ? frontTexture : backTexture);

    internal void RefreshShirt()
    {
        if (manager == null || shirt == null || shirts.Count == 0) return;
        var data = manager.nowCharacterData;
        var body = manager.infoPage.FindBodyRow(data.bodyType);
        if (body == null) { shirt.enabled = false; return; }
        bool female = body.Int(5) >= 4;
        int collar = data.bodyDetailFlags & 0xffff;
        string kit = CharacterInfoController.DefaultUniformStem + "_" + CharacterInfoController.DefaultUniformMember;
        string key = kit + (collar != 0 ? "_01" : "_00");
        if (data.uniformSleeve != 0) {
            var sleeve = NativeUniformFashion.Resolve(kit + "_slv", body);
            string sleeveKey = sleeve == null ? null : Path.GetFileNameWithoutExtension(sleeve.Uniform) + "_" + kit + "_00";
            if (sleeveKey != null && shirts.ContainsKey(sleeveKey)) key = sleeveKey;
        }
        // Hem changes the model below this crop and never selects a different portrait shirt.
        float width = manager.infoPage.GetIconBodyWidth(data.bodyType, data.chestSize);
        float referenceWidth = manager.infoPage.GetIconBodyWidth(female ? 2 : 1, data.chestSize);
        float ratio = width > 0 && referenceWidth > 0 ? width / referenceWidth : 1f;
        shirt.rectTransform.localScale = originalShirtScale;
        Color32 skin = manager.infoPage.GetSelectedSkinColor();
        if (!hasSkin || !skin.Equals(appliedSkin))
        {
            foreach (var icon in shirts.Values.Concat(bodyShirts.Values)) TintSkin(icon, skin);
            appliedSkin = skin; hasSkin = true;
        }
        if (preparedFemale != female) { PreloadBodyShirts(skin); preparedFemale = female; }
        shirt.texture = BodyShirt(key, ratio, skin).Texture;
        // DDS rows retain their native top-to-bottom order; UI textures flip vertically.
        shirt.uvRect = new Rect(0, 1, 1, -1);
        shirt.enabled = true;
    }

    void PreloadBodyShirts(Color32 skin)
    {
        for (int type = 1; type <= 8; type++)
        {
            var body = manager.infoPage.FindBodyRow(type);
            if (body == null) continue;
            bool female = body.Int(5) >= 4;
            for (int chest = -1; chest < 3; chest++)
            {
                float width = manager.infoPage.GetIconBodyWidth(type, chest);
                float reference = manager.infoPage.GetIconBodyWidth(female ? 2 : 1, chest);
                if (width <= 0 || reference <= 0) continue;
                foreach (string key in shirts.Keys.ToArray()) BodyShirt(key, width / reference, skin);
            }
        }
    }
    ShirtIcon BodyShirt(string key, float scale, Color32 skin)
    {
        if (Mathf.Approximately(scale, 1)) return shirts[key];
        string id = key + "|" + scale.ToString("R", CultureInfo.InvariantCulture);
        if (bodyShirts.TryGetValue(id, out var existing)) return existing;
        var source = shirts[key];
        var texture = new Texture2D(source.Texture.width, source.Texture.height, TextureFormat.RGBA32, false, false)
        { name = source.Texture.name + " body " + scale.ToString("F3", CultureInfo.InvariantCulture), filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        var result = new ShirtIcon { Texture = texture, Pixels = source.Pixels, Mask = source.Mask, BodyScale = scale };
        TintSkin(result, skin); bodyShirts.Add(id, result);
        return result;
    }

    static ShirtIcon LoadShirt(string stem)
    {
        string path = Path.Combine(DumpDataManager.Dx11Path, "menu", "200_icon", "10_icon_chr", "uniform", stem + "_l.g4tx");
        var layers = RawTexture.Read(File.ReadAllBytes(path), path);
        var cloth = layers.Single(t => t.Name == stem + "_1");
        var mask = layers.Single(t => t.Name == stem + "_2");
        var size = G4TextureParser.ParseDDS(cloth.Dds, cloth.Name);
        var maskSize = G4TextureParser.ParseDDS(mask.Dds, mask.Name);
        if (size.Width != maskSize.Width || size.Height != maskSize.Height)
            throw new InvalidDataException("Shirt and skin icon dimensions differ: " + path);
        var pixels = CharacterManager.ReadDdsBasePixels(cloth.Dds, cloth.Name, false);
        var maskPixels = CharacterManager.ReadDdsBasePixels(mask.Dds, mask.Name, false);
        var texture = new Texture2D(size.Width, size.Height, TextureFormat.RGBA32, false, false)
        { name = stem, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
        return new ShirtIcon { Texture = texture, Pixels = pixels, Mask = maskPixels };
    }
    static void TintSkin(ShirtIcon icon, Color32 skin)
    {
        var pixels = (Color32[])icon.Pixels.Clone();
        for (int i = 0; i < pixels.Length; i++)
        {
            int alpha = icon.Mask[i].a * skin.a / 255;
            if (alpha == 0) continue;
            var dst = pixels[i];
            int outAlpha = alpha + dst.a * (255 - alpha) / 255;
            pixels[i] = new Color32(Blend(skin.r, dst.r, alpha, dst.a, outAlpha),
                Blend(skin.g, dst.g, alpha, dst.a, outAlpha), Blend(skin.b, dst.b, alpha, dst.a, outAlpha), (byte)outAlpha);
        }
        if (!Mathf.Approximately(icon.BodyScale, 1))
            pixels = FitBodyWidth(pixels, icon.Mask, icon.Texture.width, icon.Texture.height, icon.BodyScale);
        icon.Texture.SetPixels32(pixels);
        icon.Texture.Apply(false, false);
    }
    static Color32[] FitBodyWidth(Color32[] pixels, Color32[] mask, int width, int height, float bodyScale)
    {
        // Preserve the neck and its outline. Scale the garment on either side
        // using the baked torso width instead of stretching the head attachment.
        float center = (width - 1) * .5f, neckRadius = 0;
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            if (mask[y * width + x].a > 127) neckRadius = Mathf.Max(neckRadius, Mathf.Abs(x - center));
        neckRadius += width * .03f;
        var result = new Color32[pixels.Length];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            float distance = Mathf.Abs(x - center);
            float sourceDistance = distance <= neckRadius ? distance : neckRadius + (distance - neckRadius) / bodyScale;
            float sourceX = center + Mathf.Sign(x - center) * sourceDistance;
            if (sourceX < 0 || sourceX > width - 1) continue;
            int left = Mathf.FloorToInt(sourceX), right = Math.Min(left + 1, width - 1);
            result[y * width + x] = Color32.Lerp(pixels[y * width + left], pixels[y * width + right], sourceX - left);
        }
        return result;
    }

    static byte Blend(int src, int dst, int alpha, int dstAlpha, int outAlpha) =>
        outAlpha == 0 ? (byte)0 : (byte)((src * alpha + dst * dstAlpha * (255 - alpha) / 255) / outAlpha);
    static void SetVisible(RawImage image, bool visible) { if (image != null) image.enabled = visible; }

    public void OnClickUploadIcon()
    {
        Texture2D selectedFront = null, selectedBack = null;
        try
        {
            var extensions = new[] { new ExtensionFilter("Images", "png", "jpg", "jpeg") };
            string[] first = StandaloneFileBrowser.OpenFilePanel(LocalizationManager.GetText("SelectIconFront"), "", extensions, false);
            if (first == null || first.Length == 0 || string.IsNullOrWhiteSpace(first[0])) return;
            selectedFront = ReadImage(File.ReadAllBytes(first[0]), "Uploaded Icon Front");
            string[] second = StandaloneFileBrowser.OpenFilePanel(LocalizationManager.GetText("SelectIconBack"), "", extensions, false);
            selectedBack = second == null || second.Length == 0 || string.IsNullOrWhiteSpace(second[0])
                ? EmptyImage() : ReadImage(File.ReadAllBytes(second[0]), "Uploaded Icon Back");
            SetUploadedPortrait(selectedFront, selectedBack);
            selectedFront = selectedBack = null;
        }
        catch (Exception error) { Debug.LogWarning("Cannot load character icon: " + error.Message, this); }
        finally
        {
            if (selectedFront != null) CharacterManager.DestroyRuntimeObject(selectedFront);
            if (selectedBack != null) CharacterManager.DestroyRuntimeObject(selectedBack);
        }
    }
    static Texture2D ReadImage(byte[] bytes, string name)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = name, wrapMode = TextureWrapMode.Clamp };
        if (texture.LoadImage(bytes)) return texture;
        CharacterManager.DestroyRuntimeObject(texture);
        throw new InvalidDataException("Invalid PNG or JPEG image.");
    }
    static Texture2D EmptyImage()
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false, false) { name = "Empty Icon Back", wrapMode = TextureWrapMode.Clamp };
        texture.SetPixels(new[] { Color.clear, Color.clear, Color.clear, Color.clear }); texture.Apply();
        return texture;
    }
    void SetUploadedPortrait(Texture2D frontImage, Texture2D backImage)
    {
        ReleaseUploadedPortrait(); uploadedFront = frontImage; uploadedBack = backImage;
        if (front != null) { front.texture = uploadedFront; front.uvRect = new Rect(0, 0, 1, 1); front.enabled = true; }
        if (back != null) { back.texture = uploadedBack; back.uvRect = new Rect(0, 0, 1, 1); back.enabled = true; }
    }
    void ReleaseUploadedPortrait()
    {
        if (uploadedFront != null) CharacterManager.DestroyRuntimeObject(uploadedFront);
        if (uploadedBack != null) CharacterManager.DestroyRuntimeObject(uploadedBack);
        uploadedFront = uploadedBack = null;
    }
    internal void ClearUploadedPortrait()
    {
        ReleaseUploadedPortrait();
        if (front != null) front.texture = frontTexture;
        if (back != null) back.texture = backTexture;
    }
    internal void SaveUploadedPortrait(CharacterMakerDocument document)
    {
        if (!HasUploadedPortrait) return;
        document.iconFrontPng = Convert.ToBase64String(uploadedFront.EncodeToPNG());
        document.iconBackPng = uploadedBack != null ? Convert.ToBase64String(uploadedBack.EncodeToPNG()) : null;
    }
    internal void RestoreUploadedPortrait(CharacterMakerDocument document)
    {
        if (string.IsNullOrWhiteSpace(document.iconFrontPng)) { ClearUploadedPortrait(); return; }
        Texture2D image = ReadImage(Convert.FromBase64String(document.iconFrontPng), "Uploaded Icon Front");
        try
        {
            var rear = string.IsNullOrWhiteSpace(document.iconBackPng) ? EmptyImage() : ReadImage(Convert.FromBase64String(document.iconBackPng), "Uploaded Icon Back");
            SetUploadedPortrait(image, rear); image = null;
        }
        finally { if (image != null) CharacterManager.DestroyRuntimeObject(image); }
    }
    internal void CaptureUploadedPortrait(RenderTexture target, Action<RenderTexture, bool> capture)
    {
        Graphics.Blit(uploadedFront, target); capture(target, true);
        Graphics.Blit(uploadedBack, target); capture(target, false);
    }

    void ReleaseTargets()
    {
        if (front != null && front.texture == frontTexture) front.texture = null;
        if (back != null && back.texture == backTexture) back.texture = null;
        foreach (var texture in new[] { frontTexture, backTexture })
            if (texture != null) { texture.Release(); CharacterManager.DestroyRuntimeObject(texture); }
        frontTexture = backTexture = null;
    }
    void ClearShirts()
    {
        if (shirt != null) { shirt.texture = null; shirt.enabled = false; }
        foreach (var icon in shirts.Values.Concat(bodyShirts.Values)) CharacterManager.DestroyRuntimeObject(icon.Texture);
        shirts.Clear(); bodyShirts.Clear(); preparedFemale = null; hasSkin = false;
    }
    void OnDestroy() { ReleaseUploadedPortrait(); ReleaseTargets(); ClearShirts(); }
}