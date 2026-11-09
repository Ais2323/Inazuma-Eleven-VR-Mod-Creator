using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

public enum UniformPartKind { Upper, Lower, Number, Armband, Gloves, Skin, Name }

[Serializable]
public sealed partial class UniformMakerDocument
{
    public int version = 1;
    public uint targetUniformId;
    public string targetKey;
    public bool isNewUniform, freeInShop, shareAwayModels;
    public uint templateUniformId;
    public string newUniformKey, uniformName;
    public List<UniformLocalizedName> names = new();
    public string iconPngBase64;
    public string NameFor(string locale) => names?.Find(n => n.locale == locale)?.name ?? uniformName;
    public int side, role, bodyType = 1, collar, sleeve, hem, chestSize = -1, number = 10;
    public int gender;
    public Color skinColor;
    public List<UniformMakerVariant> variants = new();

    internal IEnumerable<UniformMakerVariant> EffectiveVariants()
    {
        foreach (var original in variants) {
            if (!shareAwayModels || original.side == 0) { yield return original; continue; }
            var home = variants.FirstOrDefault(v => v.side == 0 && v.role == original.role && v.bodyAttribute == original.bodyAttribute)
                ?? variants.FirstOrDefault(v => v.side == 0 && v.role == original.role && v.bodyAttribute == -1);
            if (home == null) { yield return original; continue; }
            var item = home.Copy(); item.side = 1; item.textures = original.textures;
            item.portraitIcons = original.portraitIcons; item.inheritPortraitCollar = original.inheritPortraitCollar; item.inheritPortraitSleeve = original.inheritPortraitSleeve;
            yield return item;
        }
        if (!shareAwayModels) yield break;
        foreach (var home in variants.Where(v => v.side == 0 && !variants.Any(a => a.side == 1 && a.role == v.role && a.bodyAttribute == v.bodyAttribute))) {
            var item = home.Copy(); item.side = 1; item.textures = new List<UniformMakerTexture>(); item.portraitIcons = new List<UniformMakerPortrait>(); yield return item;
        }
    }

    public static UniformMakerDocument Read(string path)
    {
        var document = JsonUtility.FromJson<UniformMakerDocument>(File.ReadAllText(path));
        if (document == null) throw new InvalidDataException("Invalid UniformMaker document.");
        document.ResolveSharedData(); document.Validate(); return document;
    }
    public void Validate()
    {
        if ((version != 1 && version != 2) || (!isNewUniform && targetUniformId == 0) ||
            (isNewUniform && (templateUniformId == 0 || string.IsNullOrWhiteSpace(newUniformKey) || string.IsNullOrWhiteSpace(uniformName))) ||
            variants == null || bodyType < 1 || bodyType > 8 ||
            side < 0 || side > 1 || role < 0 || role > 1 || collar < 0 || collar > 1 || sleeve < 0 || sleeve > 1 || hem < 0 || hem > 2 ||
            gender < 0 || gender > 2 || chestSize < -1 || chestSize > 2 || number < 0 || number > 99) throw new InvalidDataException("Invalid UniformMaker settings.");
        var keys = new HashSet<string>();
        if (names != null && (names.Any(n => n == null || string.IsNullOrWhiteSpace(n.locale)) || names.GroupBy(n => n.locale).Any(g => g.Count() > 1)))
            throw new InvalidDataException("Invalid uniform localized names.");
        if (!string.IsNullOrEmpty(iconPngBase64))
        {
            byte[] png = Convert.FromBase64String(iconPngBase64);
            if (png.Length < 24 || !png.Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ||
                Encoding.ASCII.GetString(png, 12, 4) != "IHDR") throw new InvalidDataException("Invalid uniform icon PNG.");
            long Dimension(int offset) => ((long)png[offset] << 24) | ((long)png[offset + 1] << 16) | ((long)png[offset + 2] << 8) | png[offset + 3];
            if (Dimension(16) < 1 || Dimension(16) > 8192 || Dimension(20) < 1 || Dimension(20) > 8192)
                throw new InvalidDataException("Invalid uniform icon dimensions.");
        }
        var validatedPayloads = new HashSet<string>(StringComparer.Ordinal);
        var sharedStrings = new Dictionary<string, string>(StringComparer.Ordinal);
        string Intern(string value) {
            if (value == null) return null;
            if (sharedStrings.TryGetValue(value, out var shared)) return shared;
            sharedStrings.Add(value, value); return value;
        }
        foreach (var variant in variants)
        {
            if (variant == null || variant.side < 0 || variant.side > 1 || variant.role < 0 || variant.role > 1 || variant.bodyAttribute < -1 || variant.bodyAttribute > 17 || !keys.Add(variant.side + "/" + variant.role + "/" + variant.bodyAttribute) || variant.textures == null)
                throw new InvalidDataException("Duplicate or invalid uniform variant.");
            if (variant.fbx?.IsEmpty == true) variant.fbx = null;
            if (variant.fbx != null) {
                variant.fbx.bytesBase64 = Intern(variant.fbx.bytesBase64);
                if (variant.bodyAttribute < 0) throw new InvalidDataException("FBX requires a body-specific variant."); variant.fbx.Validate(validatedPayloads);
            }
            variant.partFbx ??= new List<UniformMakerPartFbx>();
            var fbxParts = new HashSet<UniformPartKind>();
            foreach (var part in variant.partFbx) {
                if (part == null || !Enum.IsDefined(typeof(UniformPartKind), part.part) || !fbxParts.Add(part.part) || part.model == null || variant.bodyAttribute < 0)
                    throw new InvalidDataException("Invalid part FBX binding.");
                part.model.bytesBase64 = Intern(part.model.bytesBase64); part.model.Validate(validatedPayloads);
            }
            var channels = new HashSet<string>();
            var portraitKeys = new HashSet<string>();
            foreach (var portrait in variant.portraitIcons ?? new List<UniformMakerPortrait>()) {
                if (portrait == null || portrait.collar < 0 || portrait.collar > 1 || portrait.sleeve < 0 || portrait.sleeve > 1 ||
                    !portraitKeys.Add(portrait.collar + "/" + portrait.sleeve)) throw new InvalidDataException("Invalid uniform portrait setting.");
                portrait.pngBase64 = Intern(portrait.pngBase64);
                portrait.maskPngBase64 = Intern(portrait.maskPngBase64);
                if (string.IsNullOrEmpty(portrait.pngBase64) && string.IsNullOrEmpty(portrait.maskPngBase64)) throw new InvalidDataException("Missing uniform portrait image.");
                foreach (var payload in new[] { portrait.pngBase64, portrait.maskPngBase64 })
                if (!string.IsNullOrEmpty(payload) && validatedPayloads.Add(payload)) {
                    byte[] png = Convert.FromBase64String(payload);
                    if (png.Length < 24 || Encoding.ASCII.GetString(png, 1, 3) != "PNG" || Encoding.ASCII.GetString(png, 12, 4) != "IHDR")
                        throw new InvalidDataException("Invalid uniform portrait PNG.");
                    long Dimension(int offset) => ((long)png[offset] << 24) | ((long)png[offset + 1] << 16) | ((long)png[offset + 2] << 8) | png[offset + 3];
                    if (Dimension(16) < 1 || Dimension(16) > 8192 || Dimension(20) < 1 || Dimension(20) > 8192)
                        throw new InvalidDataException("Invalid uniform portrait dimensions.");
                }
            }
            foreach (var texture in variant.textures)
            {
                if (!variant.labelsConfigured && texture != null && texture.part == UniformPartKind.Number &&
                    texture.originalName?.StartsWith("name_", StringComparison.OrdinalIgnoreCase) == true)
                    texture.part = UniformPartKind.Name;
                if (texture == null || !Enum.IsDefined(typeof(UniformPartKind), texture.part) || texture.channel < 0 || texture.channel > 3 ||
                    texture.width < 1 || texture.height < 1 || texture.width > 16384 || texture.height > 16384 || texture.mipCount < 1 ||
                    texture.mipCount > 1 + (int)Math.Floor(Math.Log(Math.Max(texture.width, texture.height), 2)) ||
                    string.IsNullOrWhiteSpace(texture.originalName) || string.IsNullOrWhiteSpace(texture.bytesBase64) ||
                    (texture.extension != ".png" && texture.extension != ".dds") || !channels.Add(texture.part + "/" + texture.channel + "/" + texture.originalName))
                    throw new InvalidDataException("Invalid uniform texture.");
                texture.bytesBase64 = Intern(texture.bytesBase64);
                if (validatedPayloads.Add(texture.bytesBase64)) Convert.FromBase64String(texture.bytesBase64);
            }
        }
    }
    public void Write(string path) { Validate(); File.WriteAllText(path, JsonUtility.ToJson(CreateStorageCopy(), true), new UTF8Encoding(false)); }
}

[Serializable]
public sealed class UniformLocalizedName
{
    public string locale, name;
}

[Serializable]
public sealed class UniformMakerVariant
{
    public int side, role;
    public int bodyAttribute = -1;
    public uint clothesModelId, armbandModelId, gloveModelId;
    public uint skinModelId, numberModelId;
    public uint nameModelId;
    public bool hideName, labelsConfigured, hideUpper, hideLower;
    public List<UniformMakerPartFbx> partFbx = new();
    public uint NameModelId => labelsConfigured ? nameModelId : numberModelId;
    public bool hideSkin, hideNumber, hideArmband, hideGloves, accessoriesConfigured;
    public UniformFbxModel fbx;
    public string fbxId;
    internal UniformMakerVariant Copy() => (UniformMakerVariant)MemberwiseClone();
    public List<UniformMakerTexture> textures = new();
    public List<UniformMakerPortrait> portraitIcons = new();
    public bool inheritPortraitCollar, inheritPortraitSleeve;
    public int PortraitCollar(int value) => inheritPortraitCollar ? 0 : value;
    public int PortraitSleeve(int value) => inheritPortraitSleeve ? 0 : value;
    public bool IsHidden(UniformPartKind kind) => (fbx != null && !fbx.IsEmpty && !accessoriesConfigured && kind != UniformPartKind.Upper && kind != UniformPartKind.Lower) ||
        (kind == UniformPartKind.Upper ? hideUpper : kind == UniformPartKind.Lower ? hideLower : kind == UniformPartKind.Skin ? hideSkin : kind == UniformPartKind.Number ? hideNumber : kind == UniformPartKind.Name ? (labelsConfigured ? hideName : hideNumber) : kind == UniformPartKind.Armband ? hideArmband : kind == UniformPartKind.Gloves && hideGloves);
    public bool HasChanges => clothesModelId != 0 || armbandModelId != 0 || gloveModelId != 0 || skinModelId != 0 || numberModelId != 0 ||
        hideUpper || hideLower || partFbx.Count > 0 || hideSkin || hideNumber || hideArmband || hideGloves || hideName || nameModelId != 0 || textures.Count > 0 ||
        inheritPortraitCollar || inheritPortraitSleeve || (portraitIcons?.Count ?? 0) > 0 || (fbx != null && !fbx.IsEmpty);
}

[Serializable]
public sealed class UniformMakerPortrait
{
    public int collar, sleeve;
    public string pngBase64, dataId, maskPngBase64, maskDataId;
    internal UniformMakerPortrait Copy() => (UniformMakerPortrait)MemberwiseClone();
}

[Serializable]
public sealed class UniformMakerTexture
{
    public UniformPartKind part;
    public int channel, width, height, mipCount;
    public string originalName, fileName, extension, bytesBase64;
    public bool sharedAcrossBodies;
    internal static string TextureSuffix(string name) { int at = (name ?? "").IndexOf('_'); return at < 0 ? name : name.Substring(at); }
    internal bool MatchesName(string name) => originalName == name || sharedAcrossBodies && TextureSuffix(originalName) == TextureSuffix(name);
    public string dataId;
    internal UniformMakerTexture Copy() => (UniformMakerTexture)MemberwiseClone();
}

[Serializable]
public sealed class UniformMakerPartFbx { public UniformPartKind part; public UniformFbxModel model; public string fbxId; }
