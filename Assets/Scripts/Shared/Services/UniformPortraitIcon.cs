using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using G4Composition;
using G4Runtime;
using UnityEngine;

internal sealed class UniformPortraitIcon
{
    internal const string DirectoryPath = "menu/200_icon/10_icon_chr/uniform";
    internal string Stem;
    internal byte[] Container;
    internal RawTexture Cloth, Mask;
    internal int Width, Height;
    Color32[] clothPixels, maskPixels;

    internal static string Find(NativeUniformFashion.Selection outfit, CfgTable.Row body, int collar, int sleeve)
    {
        if (outfit == null) return null;
        var normal = NativeUniformFashion.Resolve(outfit.ModelId, body, sleeve, 0) ?? outfit;
        string model = Path.GetFileNameWithoutExtension(normal.Uniform);
        string texture = Path.GetFileNameWithoutExtension(normal.UniformTexture);
        string stem = NativeUniformFashion.IconTextureStem(model, texture);
        int attribute = (int)body.Int(4);
        int style = attribute == 8 || attribute == 9 || attribute == 13 || attribute == 17 ? 1 : 0;
        var candidates = new List<string>();
        if (sleeve != 0) candidates.Add(model + "_" + texture + "_00");
        candidates.AddRange(new[] { stem + "_" + style + collar, texture + "_" + style + collar,
            stem + "_0" + collar, texture + "_0" + collar, stem + "_00", texture + "_00" });
        return candidates.Distinct().FirstOrDefault(candidate => File.Exists(NativePath(candidate)));
    }

    internal static string NativePath(string stem) => Path.Combine(DumpDataManager.Dx11Path, DirectoryPath, stem + "_l.g4tx");

    internal static UniformPortraitIcon Load(string stem)
    {
        byte[] bytes = File.ReadAllBytes(NativePath(stem));
        var layers = RawTexture.Read(bytes, stem);
        var cloth = layers.Single(t => t.Name == stem + "_1");
        var mask = layers.Single(t => t.Name == stem + "_2");
        var size = G4TextureParser.ParseDDS(cloth.Dds, cloth.Name);
        var maskSize = G4TextureParser.ParseDDS(mask.Dds, mask.Name);
        if (size.Width != maskSize.Width || size.Height != maskSize.Height || size.MipCount != 1 || maskSize.MipCount != 1)
            throw new InvalidDataException("Unsupported uniform portrait layers: " + stem);
        return new UniformPortraitIcon { Stem = stem, Container = bytes, Cloth = cloth, Mask = mask, Width = size.Width, Height = size.Height };
    }

    internal Color32[] ClothPixels => clothPixels ??= CharacterManager.ReadDdsBasePixels(Cloth.Dds, Cloth.Name, false);
    internal Color32[] MaskPixels => maskPixels ??= CharacterManager.ReadDdsBasePixels(Mask.Dds, Mask.Name, false);

    internal Color32[] Composite(string uploadedPng, Color32 skin)
    {
        var pixels = string.IsNullOrEmpty(uploadedPng) ? (Color32[])ClothPixels.Clone() : ReadPngPixels(uploadedPng, Width, Height);
        var mask = MaskPixels;
        for (int i = 0; i < pixels.Length; i++) {
            int alpha = mask[i].a * skin.a / 255;
            if (alpha == 0) continue;
            var dst = pixels[i];
            int outAlpha = alpha + dst.a * (255 - alpha) / 255;
            byte Blend(int color, int back) => outAlpha == 0 ? (byte)0 : (byte)((color * alpha + back * dst.a * (255 - alpha) / 255) / outAlpha);
            pixels[i] = new Color32(Blend(skin.r, dst.r), Blend(skin.g, dst.g), Blend(skin.b, dst.b), (byte)outAlpha);
        }
        return pixels;
    }

    internal static byte[] NormalizeImage(byte[] bytes)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        try {
            if (!texture.LoadImage(bytes) || texture.width > 8192 || texture.height > 8192)
                throw new InvalidDataException("Invalid portrait image (maximum 8192x8192).");
            return texture.EncodeToPNG();
        }
        finally { Destroy(texture); }
    }

    internal static Color32[] ReadPngPixels(string png, int width, int height)
    {
        var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        Texture2D readback = null;
        var previous = RenderTexture.active;
        var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
        try {
            if (!source.LoadImage(Convert.FromBase64String(png))) throw new InvalidDataException("Invalid portrait image.");
            source.wrapMode = TextureWrapMode.Clamp;
            Graphics.Blit(source, target); RenderTexture.active = target;
            readback = new Texture2D(width, height, TextureFormat.RGBA32, false);
            readback.ReadPixels(new Rect(0, 0, width, height), 0, 0); readback.Apply();
            return Flip(readback.GetPixels32(), width, height);
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(target); Destroy(source); Destroy(readback); }
    }

    internal static byte[] EncodePng(Color32[] pixels, int width, int height)
    {
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false);
        try { texture.SetPixels32(Flip(pixels, width, height)); texture.Apply(); return texture.EncodeToPNG(); }
        finally { Destroy(texture); }
    }

    internal byte[] EncodeCloth(string png) => string.IsNullOrEmpty(png) ? Cloth.Dds :
        Bc7PortraitEncoder.Encode(ReadPngPixels(png, Width, Height), Width, Height, CancellationToken.None);

    internal byte[] Write(string targetStem, byte[] cloth)
    {
        var layers = new[] { Cloth, Mask }.Select((layer, i) => new RawTexture {
            Name = targetStem + "_" + (i + 1), Hash = G4TextureParser.Crc32(targetStem + "_" + (i + 1)),
            Descriptor = (byte[])layer.Descriptor.Clone(), Dds = i == 0 ? cloth : layer.Dds
        }).ToArray();
        return RawTexture.WriteNativeLayout(Container, Container, layers);
    }

    static Color32[] Flip(Color32[] source, int width, int height)
    {
        var result = new Color32[source.Length];
        for (int y = 0; y < height; y++) Array.Copy(source, y * width, result, (height - 1 - y) * width, width);
        return result;
    }
    static void Destroy(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value);
    }
}
