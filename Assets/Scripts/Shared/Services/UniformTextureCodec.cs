using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using G4Composition;
using G4Runtime;
using UnityEngine;

internal static class UniformTextureCodec
{
    internal static Texture2D Decode(byte[] bytes, string extension, int width, int height, bool linear)
    {
        Texture2D texture = null;
        try
        {
            if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
            {
                var data = G4TextureParser.ParseDDS(bytes, "Uniform texture");
                if (data.Width != width || data.Height != height) throw new InvalidDataException("Texture dimensions must be " + width + "x" + height + ".");
                texture = new Texture2D(width, height, data.Format, data.MipCount, linear);
                texture.LoadRawTextureData(data.Pixels); texture.Apply(false, true);
            }
            else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                texture = new Texture2D(2, 2, TextureFormat.RGBA32, true, linear);
                if (!texture.LoadImage(bytes, false)) throw new InvalidDataException("Cannot read PNG.");
                if (texture.width != width || texture.height != height) throw new InvalidDataException("Texture dimensions must be " + width + "x" + height + ".");
                var source = texture.GetPixels32(); var flipped = new Color32[source.Length];
                for (int y = 0; y < height; y++) Array.Copy(source, y * width, flipped, (height - 1 - y) * width, width);
                texture.SetPixels32(flipped); texture.Apply(true, true);
            }
            else throw new InvalidDataException("Only DDS and PNG are supported.");
            texture.wrapMode = TextureWrapMode.Repeat; texture.filterMode = FilterMode.Bilinear;
            return texture;
        }
        catch { Release(texture); throw; }
    }

    internal static byte[] ToDds(UniformMakerTexture saved)
    {
        byte[] bytes = Convert.FromBase64String(saved.bytesBase64);
        if (saved.extension == ".dds")
        {
            var data = G4TextureParser.ParseDDS(bytes, saved.fileName);
            if (data.Width != saved.width || data.Height != saved.height) throw new InvalidDataException("Saved DDS dimensions differ.");
            if (data.MipCount == saved.mipCount) return bytes;
        }
        var source = Decode(bytes, saved.extension, saved.width, saved.height, saved.channel != 0);
        var previous = RenderTexture.active;
        var rt = RenderTexture.GetTemporary(saved.width, saved.height, 0, RenderTextureFormat.ARGB32,
            saved.channel != 0 ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
        Texture2D readback = null;
        try
        {
            Graphics.Blit(source, rt); RenderTexture.active = rt;
            readback = new Texture2D(saved.width, saved.height, TextureFormat.RGBA32, false, false);
            readback.ReadPixels(new Rect(0, 0, saved.width, saved.height), 0, 0); readback.Apply(false, false);
            var pixels = readback.GetPixels32();
            var levels = new List<byte[]>(); int width = saved.width, height = saved.height;
            byte[] header = null;
            for (int mip = 0; mip < saved.mipCount; mip++)
            {
                byte[] encoded = Bc7PortraitEncoder.Encode(pixels, width, height, CancellationToken.None);
                if (header == null) header = encoded.Take(148).ToArray();
                levels.Add(encoded.Skip(148).ToArray());
                int nextWidth = Math.Max(1, width / 2), nextHeight = Math.Max(1, height / 2);
                var next = new Color32[nextWidth * nextHeight];
                for (int y = 0; y < nextHeight; y++) for (int x = 0; x < nextWidth; x++)
                {
                    int r = 0, g = 0, b = 0, a = 0;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                    { var c = pixels[Math.Min(height - 1, y * 2 + dy) * width + Math.Min(width - 1, x * 2 + dx)]; r += c.r; g += c.g; b += c.b; a += c.a; }
                    next[y * nextWidth + x] = new Color32((byte)(r / 4), (byte)(g / 4), (byte)(b / 4), (byte)(a / 4));
                }
                pixels = next; width = nextWidth; height = nextHeight;
            }
            BitConverter.GetBytes(saved.mipCount).CopyTo(header, 28);
            BitConverter.GetBytes(0xA1007).CopyTo(header, 8);
            BitConverter.GetBytes(saved.mipCount > 1 ? 0x401008 : 0x1000).CopyTo(header, 108);
            return header.Concat(levels.SelectMany(x => x)).ToArray();
        }
        finally { RenderTexture.active = previous; RenderTexture.ReleaseTemporary(rt); Release(source); Release(readback); }
    }
    internal static void Release(UnityEngine.Object value) { if (value == null) return; if (Application.isPlaying) UnityEngine.Object.Destroy(value); else UnityEngine.Object.DestroyImmediate(value); }
}
