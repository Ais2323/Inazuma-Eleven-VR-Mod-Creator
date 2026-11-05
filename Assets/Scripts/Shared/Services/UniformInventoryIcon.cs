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

    // Menu atlases use header offsets as well as counts. A parser that walks
    // tables from the count alone can accept a file the game's loader rejects.
    internal static byte[] WriteContainer(byte[] native, byte[] current, System.Collections.Generic.IReadOnlyList<RawTexture> textures)
    {
        ValidateHeaderOffsets(native);
        int count = BitConverter.ToUInt16(native, 0x20);
        int hashes = (BitConverter.ToUInt16(native, 4) + count * 48 + 15) & ~15;
        int names = (hashes + count * 5 + 3) & ~3;
        int prefixStart = (names + count * 2 + 3) & ~3;
        int firstName = names + Enumerable.Range(0, count).Min(i => (int)BitConverter.ToUInt16(native, names + i * 2));
        if (firstName < prefixStart || firstName > native.Length) throw new InvalidDataException("Invalid native icon string-table prefix.");
        byte[] prefix = native.Skip(prefixStart).Take(firstName - prefixStart).ToArray();
        byte[] result = RawTexture.Write(current, textures, prefix);
        int header = BitConverter.ToUInt16(result, 4);
        int outputHashes = (header + textures.Count * 48 + 15) & ~15;
        int outputIndices = outputHashes + textures.Count * 4;
        int outputNames = (outputIndices + textures.Count + 3) & ~3;
        int outputPayload = (header + BitConverter.ToInt32(result, 12) + 15) & ~15;
        void Offset(int field, int position) => BitConverter.GetBytes(checked((ushort)((position - header) / 4))).CopyTo(result, field);
        foreach (int field in new[] { 0x38, 0x3c, 0x42, 0x44 }) Offset(field, outputHashes);
        Offset(0x3e, outputPayload); Offset(0x40, outputIndices); Offset(0x46, outputNames);
        ValidateHeaderOffsets(result);
        return result;
    }

    internal static void ValidateHeaderOffsets(byte[] bytes)
    {
        int header = BitConverter.ToUInt16(bytes, 4), count = BitConverter.ToUInt16(bytes, 0x20);
        int hashes = (header + count * 48 + 15) & ~15;
        int indices = hashes + count * 4, names = (indices + count + 3) & ~3;
        int payload = (header + BitConverter.ToInt32(bytes, 12) + 15) & ~15;
        void Check(int field, int position)
        {
            if (header + BitConverter.ToUInt16(bytes, field) * 4 != position)
                throw new InvalidDataException("Uniform icon atlas header offset mismatch at 0x" + field.ToString("X2") + ".");
        }
        foreach (int field in new[] { 0x38, 0x3c, 0x42, 0x44 }) Check(field, hashes);
        Check(0x3e, payload); Check(0x40, indices); Check(0x46, names);
    }
}
