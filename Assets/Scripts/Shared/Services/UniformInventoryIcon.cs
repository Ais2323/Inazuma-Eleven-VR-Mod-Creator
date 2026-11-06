using System;
using System.IO;
using System.Linq;
using System.Threading;
using G4Composition;
using G4Runtime;
using UnityEngine;

internal static class UniformInventoryIcon
{
    internal const string RelativePath = "menu/200_icon/02_icon_item/icon_item07.g4tx";

    internal static void Export(string output, string templateKey, string targetKey, string pngBase64)
    {
        string native = Path.Combine(DumpDataManager.Dx11Path, RelativePath);
        string path = Path.Combine(output, "dx11", RelativePath);
        byte[] bytes = File.ReadAllBytes(File.Exists(path) ? path : native);
        var textures = RawTexture.Read(bytes, path);
        var original = RawTexture.Read(File.ReadAllBytes(native), native).SingleOrDefault(t => t.Name == templateKey)
            ?? throw new InvalidDataException("Missing native uniform icon: " + templateKey);
        var target = textures.SingleOrDefault(t => t.Name == targetKey);
        if (target == null)
        {
            target = new RawTexture { Name = targetKey, Hash = G4TextureParser.Crc32(targetKey), Descriptor = (byte[])original.Descriptor.Clone(), Dds = original.Dds };
            textures.Add(target);
        }
        if (!string.IsNullOrEmpty(pngBase64))
        {
            var size = G4TextureParser.ParseDDS(original.Dds, templateKey);
            if (size.MipCount != 1) throw new InvalidDataException("Unsupported uniform icon mip count.");
            var source = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            Texture2D readback = null;
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(size.Width, size.Height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            try
            {
                if (!source.LoadImage(Convert.FromBase64String(pngBase64))) throw new InvalidDataException("Invalid saved uniform icon.");
                Graphics.Blit(source, rt); RenderTexture.active = rt;
                readback = new Texture2D(size.Width, size.Height, TextureFormat.RGBA32, false);
                readback.ReadPixels(new Rect(0, 0, size.Width, size.Height), 0, 0); readback.Apply();
                var pixels = readback.GetPixels32(); var flipped = new Color32[pixels.Length];
                for (int y = 0; y < size.Height; y++) Array.Copy(pixels, y * size.Width, flipped, (size.Height - 1 - y) * size.Width, size.Width);
                target.Dds = Bc7PortraitEncoder.Encode(flipped, size.Width, size.Height, CancellationToken.None);
            }
            finally
            {
                RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt);
                if (Application.isPlaying) { UnityEngine.Object.Destroy(source); UnityEngine.Object.Destroy(readback); }
                else { UnityEngine.Object.DestroyImmediate(source); UnityEngine.Object.DestroyImmediate(readback); }
            }
        }
        byte[] result = WriteContainer(File.ReadAllBytes(native), bytes, textures);
        Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, result);
    }

    internal static byte[] WriteContainer(byte[] native, byte[] current, System.Collections.Generic.IReadOnlyList<RawTexture> textures)
        => RawTexture.WriteNativeLayout(native, current, textures);

    internal static void ValidateHeaderOffsets(byte[] bytes) => RawTexture.ValidateNativeLayout(bytes);
}
