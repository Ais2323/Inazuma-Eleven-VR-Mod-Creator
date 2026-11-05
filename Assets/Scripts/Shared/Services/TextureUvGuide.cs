using System;
using System.Collections.Generic;
using System.IO;
using G4Composition;
using G4Runtime;
using UnityEngine;

internal static class TextureUvGuide
{
    internal static byte[] Create(RawTexture texture, IEnumerable<G4ModelParser.Part> parts)
    {
        if (texture.Dds == null || texture.Dds.Length < 20 ||
            texture.Dds[0] != (byte)'D' || texture.Dds[1] != (byte)'D' ||
            texture.Dds[2] != (byte)'S' || texture.Dds[3] != (byte)' ')
            throw new InvalidDataException(texture.Name + " 不是有效的 DDS。");
        int height = BitConverter.ToInt32(texture.Dds, 12);
        int width = BitConverter.ToInt32(texture.Dds, 16);
        if (width < 1 || height < 1 || width > 16384 || height > 16384)
            throw new InvalidDataException(texture.Name + " 的 DDS 尺寸無效。");

        // UV drawings are editing guides, independent of the game's DDS resolution.
        float uvScale = Mathf.Max(1f, 2048f / Mathf.Max(width, height));
        width = Mathf.RoundToInt(width * uvScale); height = Mathf.RoundToInt(height * uvScale);
        var pixels = new Color32[checked(width * height)];
        foreach (var part in parts)
        {
            if (part.UV == null || part.UV.Length != part.Positions.Length)
                throw new InvalidDataException(part.Name + " 的 UV 數量無效。");
            if (part.Indices == null || part.Indices.Length % 3 != 0)
                throw new InvalidDataException(part.Name + " 的三角形索引無效。");
            for (int i = 0; i < part.Indices.Length; i += 3)
            {
                Vector2 a = part.UV[part.Indices[i]];
                Vector2 b = part.UV[part.Indices[i + 1]];
                Vector2 c = part.UV[part.Indices[i + 2]];
                DrawRepeatedUvLine(pixels, width, height, a, b);
                DrawRepeatedUvLine(pixels, width, height, b, c);
                DrawRepeatedUvLine(pixels, width, height, c, a);
            }
        }

        var image = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        try
        {
            image.SetPixels32(FlipPixelRows(pixels, width, height));
            image.Apply(false, false);
            return image.EncodeToPNG();
        }
        finally
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(image); else UnityEngine.Object.DestroyImmediate(image);
        }
    }

    private static void DrawRepeatedUvLine(Color32[] pixels, int width, int height, Vector2 a, Vector2 b)
    {
        if (!IsFinite(a) || !IsFinite(b)) throw new InvalidDataException("UV 包含非有限數值。");
        int minTileX = Mathf.CeilToInt(Mathf.Min(a.x, b.x) - 1f);
        int maxTileX = Mathf.FloorToInt(Mathf.Max(a.x, b.x));
        int minTileY = Mathf.CeilToInt(Mathf.Min(a.y, b.y) - 1f);
        int maxTileY = Mathf.FloorToInt(Mathf.Max(a.y, b.y));
        if ((long)maxTileX - minTileX > 128 || (long)maxTileY - minTileY > 128)
            throw new InvalidDataException("UV Repeat 範圍過大。");

        for (int tileY = minTileY; tileY <= maxTileY; tileY++)
        for (int tileX = minTileX; tileX <= maxTileX; tileX++)
        {
            Vector2 start = a - new Vector2(tileX, tileY);
            Vector2 end = b - new Vector2(tileX, tileY);
            if (!ClipUvLine(ref start, ref end)) continue;
            int x0 = Mathf.Clamp(Mathf.RoundToInt(start.x * (width - 1)), 0, width - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(start.y * (height - 1)), 0, height - 1);
            int x1 = Mathf.Clamp(Mathf.RoundToInt(end.x * (width - 1)), 0, width - 1);
            int y1 = Mathf.Clamp(Mathf.RoundToInt(end.y * (height - 1)), 0, height - 1);
            DrawPixelLine(pixels, width, height, x0, y0, x1, y1);
        }
    }

    private static bool ClipUvLine(ref Vector2 a, ref Vector2 b)
    {
        float t0 = 0f, t1 = 1f;
        float dx = b.x - a.x, dy = b.y - a.y;
        if (!ClipTest(-dx, a.x, ref t0, ref t1) || !ClipTest(dx, 1f - a.x, ref t0, ref t1) ||
            !ClipTest(-dy, a.y, ref t0, ref t1) || !ClipTest(dy, 1f - a.y, ref t0, ref t1)) return false;
        Vector2 origin = a;
        a = origin + new Vector2(dx, dy) * t0;
        b = origin + new Vector2(dx, dy) * t1;
        return true;
    }

    private static bool ClipTest(float p, float q, ref float t0, ref float t1)
    {
        if (Mathf.Approximately(p, 0f)) return q >= 0f;
        float r = q / p;
        if (p < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
        else { if (r < t0) return false; if (r < t1) t1 = r; }
        return true;
    }

    private static void DrawPixelLine(Color32[] pixels, int width, int height, int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        var black = new Color32(0, 0, 0, 255);
        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height) pixels[y0 * width + x0] = black;
            if (x0 == x1 && y0 == y1) break;
            int doubled = error * 2;
            if (doubled >= dy) { error += dy; x0 += sx; }
            if (doubled <= dx) { error += dx; y0 += sy; }
        }
    }

    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y);
    }

    private static Color32[] FlipPixelRows(Color32[] source, int width, int height)
    {
        var output = new Color32[source.Length];
        for (int y = 0; y < height; y++)
            Array.Copy(source, y * width, output, (height - 1 - y) * width, width);
        return output;
    }

}
