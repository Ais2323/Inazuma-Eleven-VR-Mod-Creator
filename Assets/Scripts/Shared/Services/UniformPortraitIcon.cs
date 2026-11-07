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

    internal Color32[] Composite(string uploadedPng, Color32 skin, string maskPng = null)
    {
        var pixels = string.IsNullOrEmpty(uploadedPng) ? (Color32[])ClothPixels.Clone() : ReadPngPixels(uploadedPng, Width, Height);
        var mask = string.IsNullOrEmpty(maskPng) ? MaskPixels : ReadPngPixels(maskPng, Width, Height);
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

    internal byte[] EncodeMask(string png) => string.IsNullOrEmpty(png) ? Mask.Dds :
        Bc7PortraitEncoder.Encode(ReadPngPixels(png, Width, Height), Width, Height, CancellationToken.None);

    static readonly Dictionary<string, UniformPortraitIcon> writeTemplates = new();

    internal byte[] Write(string targetStem, byte[] cloth, byte[] mask = null)
    {
        mask ??= Mask.Dds;
        // Keep the native table, offsets and payload alignment intact. A simple icon
        // and a sleeve composite need different, equal-width native name templates.
        var template = this;
        if (Stem.Length != targetStem.Length || Cloth.Dds.Length != cloth.Length || Mask.Dds.Length != mask.Length) {
            string key = targetStem.Length + "|" + Width + "|" + Height + "|" + cloth.Length + "|" + mask.Length;
            if (!writeTemplates.TryGetValue(key, out template)) {
                foreach (string path in Directory.GetFiles(Path.Combine(DumpDataManager.Dx11Path, DirectoryPath), "*_l.g4tx").OrderBy(p => p, StringComparer.Ordinal)) {
                    string stem = Path.GetFileName(path).Substring(0, Path.GetFileName(path).Length - 7);
                    if (stem.Length != targetStem.Length) continue;
                    try {
                        var candidate = Load(stem);
                        if (candidate.Width != Width || candidate.Height != Height || candidate.Cloth.Dds.Length != cloth.Length || candidate.Mask.Dds.Length != mask.Length) continue;
                        template = candidate; break;
                    }
                    catch (InvalidDataException) { }
                    catch (InvalidOperationException) { }
                }
                if (template == null) throw new InvalidDataException("No equal-width native portrait template: " + targetStem);
                writeTemplates.Add(key, template);
            }
        }
        byte[] result = (byte[])template.Container.Clone();
        var layers = RawTexture.Read(result, template.Stem);
        int header = BitConverter.ToUInt16(result, 4);
        int hashes = (header + layers.Count * 48 + 15) & ~15;
        int names = (hashes + layers.Count * 5 + 3) & ~3;
        int payload = (header + BitConverter.ToInt32(result, 12) + 15) & ~15;
        for (int i = 0; i < layers.Count; i++) {
            string suffix = layers[i].Name.Substring(template.Stem.Length);
            string name = targetStem + suffix;
            var nameBytes = System.Text.Encoding.ASCII.GetBytes(name);
            int nameAt = names + BitConverter.ToUInt16(result, names + i * 2);
            Buffer.BlockCopy(nameBytes, 0, result, nameAt, nameBytes.Length);
            Buffer.BlockCopy(BitConverter.GetBytes(G4TextureParser.Crc32(name)), 0, result, hashes + i * 4, 4);
            byte[] dds = suffix == "_1" ? cloth : mask;
            int dataAt = payload + BitConverter.ToInt32(result, header + i * 48 + 4);
            Buffer.BlockCopy(dds, 0, result, dataAt, dds.Length);
        }
        result = RawTexture.SortLookupIndices(result);
        RawTexture.ValidateNativeLayout(result);
        var check = RawTexture.Read(result, targetStem);
        if (check.Any(layer => !layer.Dds.SequenceEqual(layer.Name.EndsWith("_1", StringComparison.Ordinal) ? cloth : mask)))
            throw new InvalidDataException("Uniform portrait payload changed during binding.");
        return result;
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
