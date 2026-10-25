using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using G4Composition;
using G4Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using SFB;

public partial class CharacterManager : MonoBehaviour
{
    private sealed class TextureMaterialBinding
    {
        public Renderer Renderer;
        public Material Original;
        public Material Replacement;
    }

    private sealed class ImportedTexture
    {
        public string SourceId;
        public string Role;
        public byte[] Dds;
        public Texture2D Preview;
        public int AvatarAtlasVersion;
    }

    private readonly Dictionary<string, ImportedTexture> importedTextures = new();
    private readonly Dictionary<int, List<TextureMaterialBinding>> previewMaterialBindings = new();

    internal void ExportTextureAndUV(int option)
    {
        if (!TryGetOptionSelection(option, out string sourceId, out Transform[] selectedTransforms))
        {
            Debug.LogError("請先選擇 Head、Hair 或 Other。");
            return;
        }
        if (string.IsNullOrEmpty(sourceId) || selectedTransforms == null || selectedTransforms.Length == 0)
        {
            Debug.LogError("尚未選擇 " + OptionName(option) + " 模型。");
            return;
        }

        string[] folders = StandaloneFileBrowser.OpenFolderPanel("選擇貼圖與 UV 輸出資料夾", "", false);
        if (folders == null || folders.Length == 0 || string.IsNullOrEmpty(folders[0])) return;

        try
        {
            string avatarCode = GetAvatarSource(option);
            RawHeadAsset source = string.IsNullOrWhiteSpace(avatarCode)
                ? RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path, FindFaceModel(sourceId), CancellationToken.None)
                : AvatarCompositionAsset.Build(avatarCode);
            if (source.TextureContainer == null)
                throw new FileNotFoundException("找不到 G4TX：" + sourceId);

            var selectedNames = new HashSet<string>(
                selectedTransforms.Where(item => item != null).Select(item => item.name),
                StringComparer.OrdinalIgnoreCase);
            var texturesByHash = source.Textures.ToDictionary(texture => texture.Hash);
            var usedTextures = new Dictionary<uint, RawTexture>();
            var baseTextureParts = new Dictionary<uint, List<G4ModelParser.Part>>();
            foreach (var part in source.Preview.Parts)
            {
                if (!selectedNames.Contains(part.Name)) continue;
                if (!HasUv(source, part))
                    throw new InvalidDataException(part.Name + " 沒有 UV 資料。");
                if (part.Material < 0 || part.Material >= source.Preview.Materials.Count)
                    throw new InvalidDataException(part.Name + " 的材質索引無效。");

                foreach (uint hash in source.Preview.Materials[part.Material].TextureHashes.Distinct())
                {
                    if (!texturesByHash.TryGetValue(hash, out var texture))
                        throw new InvalidDataException("G4TX 找不到材質引用的貼圖：" + hash.ToString("X8"));
                    string role = TextureRole(texture.Name);
                    if (TextureChannel(role) == null) continue;
                    usedTextures[hash] = texture;
                    if (TextureChannel(role) == "base")
                    {
                        if (!baseTextureParts.TryGetValue(hash, out var parts))
                            baseTextureParts.Add(hash, parts = new List<G4ModelParser.Part>());
                        if (!parts.Contains(part)) parts.Add(part);
                    }
                }
            }

            if (baseTextureParts.Count == 0)
                throw new InvalidDataException("找不到無後綴主貼圖。");

            foreach (var item in usedTextures)
            {
                RawTexture texture = item.Value;
                string role = TextureRole(texture.Name);
                byte[] dds = texture.Dds;
                if (importedTextures.TryGetValue(ImportKey(option, role), out var imported) &&
                    imported.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
                    dds = imported.Dds;
                File.WriteAllBytes(Path.Combine(folders[0], SafeFileName(texture.Name) + ".dds"), dds);
            }

            foreach (var item in baseTextureParts)
            {
                RawTexture texture = texturesByHash[item.Key];
                byte[] dds = texture.Dds;
                string baseRole = TextureRole(texture.Name);
                if (importedTextures.TryGetValue(ImportKey(option, baseRole), out var imported) &&
                    imported.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
                    dds = imported.Dds;
                var uvTexture = new RawTexture { Name = texture.Name, Dds = dds };
                File.WriteAllBytes(
                    Path.Combine(folders[0], SafeFileName(texture.Name) + "_UV.png"),
                    CreateUvPng(uvTexture, item.Value));
            }

            Debug.Log("貼圖與 UV 已輸出：" + folders[0]);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }
    private static bool HasUv(RawHeadAsset source, G4ModelParser.Part part)
    {
        RawMesh mesh = source.Meshes[part.SourceMeshIndex];
        byte[] layout = source.Layouts[mesh.Layout];
        for (int i = 0; i < layout[1]; i++)
            if (layout[8 + i * 8] == 10) return true;
        return false;
    }

    private static byte[] CreateUvPng(RawTexture texture, IEnumerable<G4ModelParser.Part> parts)
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
            if (Application.isPlaying) Destroy(image); else DestroyImmediate(image);
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

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    internal void ImportTexture(int option)
    {
        if (!TryGetOptionSelection(option, out string sourceId, out Transform[] selected))
        {
            Debug.LogError("請先選擇 Head、Hair 或 Other。");
            return;
        }

        if (string.IsNullOrEmpty(sourceId) || selected == null || selected.Length == 0)
        {
            Debug.LogError("尚未選擇 " + OptionName(option) + " 模型。");
            return;
        }

        string role = GetClickedTextureRole(option, selected);
        if (role == null)
        {
            Debug.LogError("無法判斷要匯入主貼圖、OC 或 SPM。");
            return;
        }

        string[] paths = StandaloneFileBrowser.OpenFilePanel(
            "選擇 DDS 或 PNG 貼圖", "",
            new[] { new ExtensionFilter("貼圖", "dds", "png") }, false);
        if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;

        Texture2D png = null;
        Texture2D preview = null;
        try
        {
            RawTexture original = FindCurrentTexture(option, sourceId, selected, role);
            var originalInfo = G4TextureParser.ParseDDS(original.Dds, original.Name);
            bool linear = role.EndsWith("oc", StringComparison.OrdinalIgnoreCase) ||
                role.EndsWith("spm", StringComparison.OrdinalIgnoreCase);
            byte[] importedDds;
            string extension = Path.GetExtension(paths[0]);
            if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
            {
                byte[] sourceDds = File.ReadAllBytes(paths[0]);
                var importedInfo = G4TextureParser.ParseDDS(sourceDds, paths[0]);
                if (originalInfo.Width != importedInfo.Width || originalInfo.Height != importedInfo.Height)
                    throw new InvalidDataException("DDS 尺寸必須是 " + originalInfo.Width + "x" + originalInfo.Height + "。");
                Color32[] basePixels = ReadDdsBasePixels(sourceDds, paths[0], linear);
                importedDds = EncodeBc7MipDds(basePixels, originalInfo.Width, originalInfo.Height, originalInfo.MipCount);
            }
            else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                png = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
                if (!png.LoadImage(File.ReadAllBytes(paths[0]), false))
                    throw new InvalidDataException("無法讀取 PNG：" + paths[0]);
                if (png.width != originalInfo.Width || png.height != originalInfo.Height)
                    throw new InvalidDataException("PNG 尺寸必須是 " + originalInfo.Width + "x" + originalInfo.Height + "。");
                // PNG GetPixels32 starts at the bottom; native DDS data starts at the top.
                importedDds = EncodeBc7MipDds(FlipPixelRows(png.GetPixels32(), png.width, png.height), png.width, png.height, originalInfo.MipCount);
            }
            else throw new InvalidDataException("只支援 DDS 或 PNG。");

            preview = CreatePreviewTexture(importedDds, original.Name, linear);
            ClearImportedTexture(option, role, false);
            var imported = new ImportedTexture
            {
                SourceId = sourceId,
                Role = role,
                Dds = importedDds,
                Preview = preview,
                AvatarAtlasVersion = 1
            };
            preview = null;
            importedTextures.Add(ImportKey(option, role), imported);
            EnsurePreviewMaterials(option, selected);
            ApplyImportedPreviews(option);
            RefreshTextureImages(option);
            Debug.Log("貼圖已匯入：" + paths[0]);
        }
        catch (Exception ex)
        {
            if (preview != null) DestroyRuntimeObject(preview);
            Debug.LogException(ex);
        }
        finally
        {
            if (png != null) DestroyRuntimeObject(png);
        }
    }

    private static string ImportKey(int option, string role) => option + ":" + role.ToLowerInvariant();

    private string GetClickedTextureRole(int option, Transform[] selected)
    {
        string baseRole = ResolveBaseRole(option, selected);
        if (baseRole == null) return null;
        GameObject selectedObject = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selectedObject == null || selectedObject.name.Equals("TextureButton", StringComparison.OrdinalIgnoreCase)) return baseRole;
        if (selectedObject.name.Equals("OCButton", StringComparison.OrdinalIgnoreCase)) return baseRole + "oc";
        if (selectedObject.name.Equals("SPMButton", StringComparison.OrdinalIgnoreCase)) return baseRole + "spm";
        return null;
    }

    private RawTexture FindCurrentTexture(int option, string sourceId, Transform[] selected, string role)
    {
        string avatarCode = GetAvatarSource(option);
        RawHeadAsset source = string.IsNullOrWhiteSpace(avatarCode)
            ? RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path, FindFaceModel(sourceId), CancellationToken.None)
            : AvatarCompositionAsset.Build(avatarCode);
        if (source.TextureContainer == null) throw new FileNotFoundException("找不到 G4TX：" + sourceId);

        var names = new HashSet<string>(selected.Where(value => value != null).Select(value => value.name), StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<uint>();
        foreach (var part in source.Preview.Parts)
        {
            if (!names.Contains(part.Name)) continue;
            foreach (uint hash in source.Preview.Materials[part.Material].TextureHashes) hashes.Add(hash);
        }
        RawTexture[] candidates = source.Textures
            .Where(texture => hashes.Contains(texture.Hash) && (string.IsNullOrWhiteSpace(avatarCode)
                ? TextureRole(texture.Name) == role : TextureChannel(TextureRole(texture.Name)) == TextureChannel(role)))
            .ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException(OptionName(option) + " 找不到唯一的 _" + role + " 貼圖，目前找到 " + candidates.Length + " 張。");
        return candidates[0];
    }

    private static string TextureRole(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        Match match = Regex.Match(name, @"_(\d{2})(oc|spm)?(?:\.\d+|\.a)?$", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Value + match.Groups[2].Value).ToLowerInvariant() : null;
    }

    private static string BaseTextureRole(string name)
    {
        string role = TextureRole(name);
        return TextureChannel(role) == "base" ? role : null;
    }

    private static string TextureChannel(string role)
    {
        if (string.IsNullOrEmpty(role)) return null;
        if (role.EndsWith("spm", StringComparison.OrdinalIgnoreCase)) return "spm";
        if (role.EndsWith("oc", StringComparison.OrdinalIgnoreCase)) return "oc";
        return Regex.IsMatch(role, @"^\d{2}$") ? "base" : null;
    }

    private bool TryGetOptionSelection(int option, out string sourceId, out Transform[] selected)
    {
        sourceId = null;
        selected = null;
        if (option == 1)
        {
            sourceId = nowCharacterData.headId;
            selected = nowCharacterData.head;
            return true;
        }
        if (option == 2)
        {
            sourceId = nowCharacterData.hairId;
            selected = nowCharacterData.hair;
            return true;
        }
        if (option == 3)
        {
            sourceId = nowCharacterData.otherId;
            selected = nowCharacterData.other;
            return true;
        }
        return false;
    }

    private static string OptionName(int option)
    {
        if (option == 1) return "Head";
        if (option == 2) return "Hair";
        if (option == 3) return "Other";
        return "模型";
    }
    private static void ValidateImportedDimensions(G4TextureParser.TextureData expected, G4TextureParser.TextureData imported)
    {
        if (expected.Width != imported.Width || expected.Height != imported.Height)
            throw new InvalidDataException("DDS 尺寸必須是 " + expected.Width + "x" + expected.Height + "。");
        if (expected.MipCount != imported.MipCount)
            throw new InvalidDataException("DDS mip 數必須是 " + expected.MipCount + "。");
    }

    private static Texture2D CreatePreviewTexture(byte[] dds, string name, bool linear)
    {
        var data = G4TextureParser.ParseDDS(dds, name);
        if (!SystemInfo.SupportsTextureFormat(data.Format))
            throw new InvalidDataException("目前 GPU 不支援 " + data.Format + "。");
        var texture = new Texture2D(data.Width, data.Height, data.Format, data.MipCount, linear)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };
        texture.LoadRawTextureData(data.Pixels);
        texture.Apply(false, true);
        return texture;
    }

    internal static Color32[] ReadDdsBasePixels(byte[] dds, string name, bool linear)
    {
        Texture2D source = null;
        Texture2D readback = null;
        RenderTexture render = null;
        RenderTexture previous = RenderTexture.active;
        try
        {
            source = CreatePreviewTexture(dds, name, linear);
            render = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, render);
            RenderTexture.active = render;
            readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readback.Apply(false, false);
            return readback.GetPixels32();
        }
        finally
        {
            RenderTexture.active = previous;
            if (render != null) RenderTexture.ReleaseTemporary(render);
            if (source != null) DestroyRuntimeObject(source);
            if (readback != null) DestroyRuntimeObject(readback);
        }
    }

    private static Color32[] FlipPixelRows(Color32[] source, int width, int height)
    {
        var output = new Color32[source.Length];
        for (int y = 0; y < height; y++)
            Array.Copy(source, y * width, output, (height - 1 - y) * width, width);
        return output;
    }

    internal static byte[] RepackLegacyAvatarHairDds(byte[] dds, Rect bounds, bool linear)
    {
        if (bounds == new Rect(0, 0, 1, 1)) return dds;
        var info = G4TextureParser.ParseDDS(dds, "legacy Avatar hair atlas");
        Color32[] source = ReadDdsBasePixels(dds, "legacy Avatar hair atlas", linear);
        var pixels = new Color32[source.Length];
        for (int y = 0; y < info.Height; y++) for (int x = 0; x < info.Width; x++)
        {
            float sx = Mathf.Clamp((bounds.x + (x + .5f) / info.Width * bounds.width) * info.Width - .5f, 0, info.Width - 1);
            float sy = Mathf.Clamp((bounds.y + (y + .5f) / info.Height * bounds.height) * info.Height - .5f, 0, info.Height - 1);
            int x0 = Mathf.FloorToInt(sx), x1 = Math.Min(x0 + 1, info.Width - 1), y0 = Mathf.FloorToInt(sy), y1 = Math.Min(y0 + 1, info.Height - 1);
            pixels[y * info.Width + x] = Color32.Lerp(Color32.Lerp(source[y0 * info.Width + x0], source[y0 * info.Width + x1], sx - x0),
                Color32.Lerp(source[y1 * info.Width + x0], source[y1 * info.Width + x1], sx - x0), sy - y0);
        }
        return EncodeBc7MipDds(pixels, info.Width, info.Height, info.MipCount);
    }

    private static byte[] EncodeBc7MipDds(Color32[] basePixels, int width, int height, int mipCount)
    {
        var levels = new List<byte[]>();
        Color32[] pixels = basePixels;
        int levelWidth = width, levelHeight = height;
        for (int mip = 0; mip < mipCount; mip++)
        {
            byte[] encoded = Bc7PortraitEncoder.Encode(pixels, levelWidth, levelHeight, CancellationToken.None);
            levels.Add(encoded.Skip(148).ToArray());
            if (mip + 1 < mipCount)
            {
                pixels = Downsample(pixels, levelWidth, levelHeight);
                levelWidth = Math.Max(1, levelWidth / 2);
                levelHeight = Math.Max(1, levelHeight / 2);
            }
        }

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("DDS "));
            writer.Write(124); writer.Write(0xA1007); writer.Write(height); writer.Write(width);
            writer.Write(((width + 3) / 4) * ((height + 3) / 4) * 16);
            writer.Write(0); writer.Write(mipCount); writer.Write(new byte[44]);
            writer.Write(32); writer.Write(4); writer.Write(System.Text.Encoding.ASCII.GetBytes("DX10")); writer.Write(new byte[20]);
            writer.Write(mipCount > 1 ? 0x401008 : 0x1000); writer.Write(new byte[16]);
            writer.Write(98); writer.Write(3); writer.Write(0); writer.Write(1); writer.Write(0);
            foreach (byte[] level in levels) writer.Write(level);
            return stream.ToArray();
        }
    }

    private static Color32[] Downsample(Color32[] source, int width, int height)
    {
        int outputWidth = Math.Max(1, width / 2), outputHeight = Math.Max(1, height / 2);
        var output = new Color32[outputWidth * outputHeight];
        for (int y = 0; y < outputHeight; y++)
        for (int x = 0; x < outputWidth; x++)
        {
            int r = 0, g = 0, b = 0, a = 0, count = 0;
            for (int oy = 0; oy < 2; oy++)
            for (int ox = 0; ox < 2; ox++)
            {
                int sx = Math.Min(width - 1, x * 2 + ox), sy = Math.Min(height - 1, y * 2 + oy);
                Color32 color = source[sy * width + sx];
                r += color.r; g += color.g; b += color.b; a += color.a; count++;
            }
            output[y * outputWidth + x] = new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), (byte)(a / count));
        }
        return output;
    }

    private void EnsurePreviewMaterials(int option, Transform[] selected)
    {
        if (previewMaterialBindings.ContainsKey(option)) return;
        var bindings = new List<TextureMaterialBinding>();
        foreach (Transform part in selected)
        {
            if (part == null) continue;
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                Material original = renderer.sharedMaterial;
                if (original == null) continue;
                var replacement = new Material(original);
                renderer.sharedMaterial = replacement;
                bindings.Add(new TextureMaterialBinding
                {
                    Renderer = renderer,
                    Original = original,
                    Replacement = replacement
                });
            }
        }
        previewMaterialBindings[option] = bindings;
    }

    private void ApplyImportedPreviews(int option)
    {
        if (!previewMaterialBindings.TryGetValue(option, out var bindings)) return;
        string baseRole = !string.IsNullOrWhiteSpace(GetAvatarSource(option)) ? ResolveBaseRole(option, null) : bindings.Select(value => value.Original)
            .Where(value => value != null)
            .Select(value => value.mainTexture)
            .Where(value => value != null)
            .Select(value => BaseTextureRole(value.name))
            .FirstOrDefault(value => value != null);
        foreach (var binding in bindings)
        {
            if (binding.Replacement == null || binding.Original == null) continue;
            binding.Replacement.mainTexture = binding.Original.mainTexture;
            binding.Replacement.SetTexture("_OcTex", binding.Original.GetTexture("_OcTex"));
            binding.Replacement.SetTexture("_SpmTex", binding.Original.GetTexture("_SpmTex"));
        }
        if (baseRole != null)
        {
            ApplyPreviewRole(option, baseRole, bindings, "_MainTex");
            ApplyPreviewRole(option, baseRole + "oc", bindings, "_OcTex");
            ApplyPreviewRole(option, baseRole + "spm", bindings, "_SpmTex");
        }
    }

    private void ApplyPreviewRole(int option, string role, List<TextureMaterialBinding> bindings, string property)
    {
        if (!importedTextures.TryGetValue(ImportKey(option, role), out var imported)) return;
        foreach (var binding in bindings)
            if (binding.Replacement != null)
                binding.Replacement.SetTexture(property, imported.Preview);
    }

    private void RefreshTextureImages(int option)
    {
        if (!TryGetOptionSelection(option, out _, out Transform[] selected)) return;
        Material[] materials = selected == null ? Array.Empty<Material>() : selected
            .Where(value => value != null)
            .SelectMany(value => value.GetComponentsInChildren<Renderer>(true))
            .Select(renderer => renderer.sharedMaterial)
            .Where(value => value != null)
            .ToArray();
        string baseRole = string.IsNullOrWhiteSpace(GetAvatarSource(option)) ? FindBaseRole(selected) : null;
        SetTextureImage(GetPartPage(option)?.textureImage, FindMaterialTexture(materials, "_MainTex", baseRole));
        SetTextureImage(GetPartPage(option)?.ocImage, FindMaterialTexture(materials, "_OcTex", baseRole == null ? null : baseRole + "oc"));
        SetTextureImage(GetPartPage(option)?.spmImage, FindMaterialTexture(materials, "_SpmTex", baseRole == null ? null : baseRole + "spm"));
    }

    private static Texture FindMaterialTexture(IEnumerable<Material> materials, string property, string role)
    {
        foreach (Material material in materials)
        {
            Texture texture = material.GetTexture(property);
            if (texture != null && (role == null || TextureRole(texture.name) == role)) return texture;
        }
        return null;
    }

    private string ResolveBaseRole(int option, Transform[] selected)
    {
        // Avatar preview atlases have generated names. Use the native export
        // slot of the selected part, consistently before and after replacement.
        if (!string.IsNullOrWhiteSpace(GetAvatarSource(option)))
            return option == 1 ? "10" : option == 2 ? "20" : "30";
        return FindBaseRole(selected);
    }

    private static string FindBaseRole(Transform[] selected)
    {
        if (selected == null) return null;
        foreach (Transform part in selected)
        {
            if (part == null) continue;
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                Texture texture = renderer.sharedMaterial != null ? renderer.sharedMaterial.mainTexture : null;
                string role = texture != null ? BaseTextureRole(texture.name) : null;
                if (role != null) return role;
            }
        }
        return null;
    }
    private static void SetTextureImage(RawImage image, Texture texture)
    {
        if (image == null) return;
        image.texture = texture;
        image.uvRect = new Rect(0f, 1f, 1f, -1f);
    }

    private void ClearImportedTexture(int option, string role, bool refreshImages)
    {
        string key = ImportKey(option, role);
        if (importedTextures.TryGetValue(key, out var imported))
        {
            if (imported.Preview != null) DestroyRuntimeObject(imported.Preview);
            importedTextures.Remove(key);
        }
        ApplyImportedPreviews(option);
        if (refreshImages) RefreshTextureImages(option);
    }

    private void ClearImportedTextures(int option, bool refreshImages)
    {
        string prefix = option + ":";
        foreach (string key in importedTextures.Keys.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            if (importedTextures[key].Preview != null) DestroyRuntimeObject(importedTextures[key].Preview);
            importedTextures.Remove(key);
        }
        if (previewMaterialBindings.TryGetValue(option, out var bindings))
        {
            foreach (var binding in bindings)
            {
                if (binding.Renderer != null) binding.Renderer.sharedMaterial = binding.Original;
                if (binding.Replacement != null) DestroyRuntimeObject(binding.Replacement);
            }
            previewMaterialBindings.Remove(option);
        }
        if (refreshImages) RefreshTextureImages(option);
    }

    private byte[] ApplySelectedTextures(
        byte[] metadata,
        byte[] geometry,
        byte[] container,
        IReadOnlyDictionary<int, RawHeadAsset> optionSources)
    {
        G4ModelParser.Model model = G4ModelParser.Parse(metadata, geometry, "輸出模型");
        byte[] output = container;
        foreach (var entry in optionSources.OrderBy(value => value.Key))
        {
            int option = entry.Key;
            RawHeadAsset source = entry.Value;
            TryGetOptionSelection(option, out string sourceId, out Transform[] selected);
            var selectedNames = new HashSet<string>(
                (selected ?? Array.Empty<Transform>()).Where(value => value != null).Select(value => value.name),
                StringComparer.OrdinalIgnoreCase);
            var sourceHashes = new HashSet<uint>();
            foreach (var part in source.Preview.Parts.Where(value => selectedNames.Contains(value.Name)))
                foreach (uint hash in source.Preview.Materials[part.Material].TextureHashes)
                    sourceHashes.Add(hash);

            var destinationHashes = new HashSet<uint>();
            foreach (var part in OutputPartsForOption(model, option))
            {
                if (part.Material < 0 || part.Material >= model.Materials.Count)
                    throw new InvalidDataException(OptionName(option) + " 的輸出材質索引無效。");
                foreach (uint hash in model.Materials[part.Material].TextureHashes)
                    destinationHashes.Add(hash);
            }

            foreach (string channel in new[] { "base", "oc", "spm" })
            {
                RawTexture[] candidates = source.Textures
                    .Where(value => sourceHashes.Contains(value.Hash) && TextureChannel(TextureRole(value.Name)) == channel)
                    .ToArray();
                if (candidates.Length == 0) continue;
                if (candidates.Length != 1)
                    throw new InvalidDataException(OptionName(option) + " 找到多張 " + channel + " 貼圖。");
                RawTexture sourceTexture = candidates[0];
                string role = TextureRole(sourceTexture.Name);
                byte[] dds = sourceTexture.Dds;
                if (importedTextures.TryGetValue(ImportKey(option, role), out var imported) &&
                    imported.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
                    dds = imported.Dds;
                output = ReplaceG4TxTexture(
                    output, channel, dds, sourceTexture.Descriptor, destinationHashes);
            }
        }
        return output;
    }

    private static IEnumerable<G4ModelParser.Part> OutputPartsForOption(G4ModelParser.Model model, int option)
    {
        if (option == 1)
            return model.Parts.Where(value => IsFacePart(value.Name));
        G4ModelParser.Part[] nonFace = model.Parts.Where(value => !IsFacePart(value.Name)).ToArray();
        int index = option == 2 ? 0 : 1;
        return index < nonFace.Length ? new[] { nonFace[index] } : Array.Empty<G4ModelParser.Part>();
    }

    private static bool IsFacePart(string name)
    {
        return name.StartsWith("eye_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("mouth_", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReplaceG4TxTexture(
        byte[] container,
        string channel,
        byte[] dds,
        byte[] sourceDescriptor,
        HashSet<uint> materialHashes)
    {
        List<RawTexture> textures = RawTexture.Read(container, "匯出 G4TX");
        int[] matches = textures.Select((texture, index) => new { texture, index })
            .Where(value => materialHashes.Contains(value.texture.Hash) &&
                TextureChannel(TextureRole(value.texture.Name)) == channel)
            .Select(value => value.index).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("輸出 G4TX 必須剛好找到一張 " + channel + " 貼圖，目前找到 " + matches.Length + " 張。");
        int index = matches[0];
        G4TextureParser.ParseDDS(dds, "匯入 " + channel);
        textures[index].Dds = dds;
        if (sourceDescriptor == null || sourceDescriptor.Length != 48)
            throw new InvalidDataException("來源 G4TX descriptor 無效。");
        textures[index].Descriptor = (byte[])sourceDescriptor.Clone();
        return WriteG4TxPreservingTable(container, textures);
    }

    private static byte[] WriteG4TxPreservingTable(byte[] template, IReadOnlyList<RawTexture> textures)
    {
        if (template == null || template.Length < 0x60)
            throw new InvalidDataException("G4TX 範本太小。");
        int count = BitConverter.ToUInt16(template, 0x20);
        if (count != textures.Count)
            throw new InvalidDataException("G4TX 貼圖數量與原生表格不一致。");
        int payload = checked((0x60 + BitConverter.ToInt32(template, 12) + 15) & ~15);
        if (payload < 0x60 || payload > template.Length)
            throw new InvalidDataException("G4TX payload offset 無效。");

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(template, 0, payload);
            for (int i = 0; i < textures.Count; i++)
            {
                while (stream.Position % 16 != 0) writer.Write((byte)0);
                int objectOffset = checked((int)stream.Position - payload);
                writer.Write(textures[i].Dds);
                int objectSize = textures[i].Dds.Length;
                long end = stream.Position;
                stream.Position = 0x60 + i * 48;
                writer.Write(textures[i].Descriptor);
                stream.Position = 0x64 + i * 48;
                writer.Write(objectOffset);
                writer.Write(objectSize);
                stream.Position = end;
            }
            while (stream.Position % 16 != 0) writer.Write((byte)0);
            int outputSize = checked((int)stream.Length);
            stream.Position = 0x2c;
            writer.Write(outputSize - payload);
            byte[] output = stream.ToArray();
            RawTexture.Read(output, "匯出 G4TX 驗證");
            return output;
        }
    }

}
