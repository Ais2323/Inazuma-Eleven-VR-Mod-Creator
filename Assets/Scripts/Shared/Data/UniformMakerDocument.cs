using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

public enum UniformPartKind { Upper, Lower, Number, Armband, Gloves }

[Serializable]
public sealed class UniformMakerDocument
{
    public int version = 1;
    public uint targetUniformId;
    public string targetKey;
    public bool isNewUniform, freeInShop;
    public uint templateUniformId;
    public string newUniformKey, uniformName;
    public List<UniformLocalizedName> names = new();
    public string iconPngBase64;
    public string NameFor(string locale) => names?.Find(n => n.locale == locale)?.name ?? uniformName;
    public int side, role, bodyType = 1, collar, sleeve, hem, chestSize = -1, number = 10;
    public Color skinColor;
    public List<UniformMakerVariant> variants = new();

    public static UniformMakerDocument Read(string path)
    {
        var document = JsonUtility.FromJson<UniformMakerDocument>(File.ReadAllText(path));
        if (document == null) throw new InvalidDataException("Invalid UniformMaker document.");
        document.Validate(); return document;
    }
    public void Validate()
    {
        if (version != 1 || (!isNewUniform && targetUniformId == 0) ||
            (isNewUniform && (templateUniformId == 0 || string.IsNullOrWhiteSpace(newUniformKey) || string.IsNullOrWhiteSpace(uniformName))) ||
            variants == null || bodyType < 1 || bodyType > 8 ||
            side < 0 || side > 1 || role < 0 || role > 1 || collar < 0 || collar > 1 || sleeve < 0 || sleeve > 1 || hem < 0 || hem > 2 ||
            chestSize < -1 || chestSize > 2 || number < 0 || number > 99) throw new InvalidDataException("Invalid UniformMaker settings.");
        var keys = new HashSet<int>();
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
        foreach (var variant in variants)
        {
            if (variant == null || variant.side < 0 || variant.side > 1 || variant.role < 0 || variant.role > 1 || !keys.Add(variant.side * 2 + variant.role) || variant.textures == null)
                throw new InvalidDataException("Duplicate or invalid uniform variant.");
            var channels = new HashSet<string>();
            foreach (var texture in variant.textures)
            {
                if (texture == null || !Enum.IsDefined(typeof(UniformPartKind), texture.part) || texture.channel < 0 || texture.channel > 2 ||
                    texture.width < 1 || texture.height < 1 || texture.width > 16384 || texture.height > 16384 || texture.mipCount < 1 ||
                    texture.mipCount > 1 + (int)Math.Floor(Math.Log(Math.Max(texture.width, texture.height), 2)) ||
                    string.IsNullOrWhiteSpace(texture.originalName) || string.IsNullOrWhiteSpace(texture.bytesBase64) ||
                    (texture.extension != ".png" && texture.extension != ".dds") || !channels.Add(texture.part + "/" + texture.channel))
                    throw new InvalidDataException("Invalid uniform texture.");
                Convert.FromBase64String(texture.bytesBase64);
            }
        }
    }
    public void Write(string path) { Validate(); File.WriteAllText(path, JsonUtility.ToJson(this, true), new UTF8Encoding(false)); }
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
    public uint clothesModelId, armbandModelId, gloveModelId;
    public List<UniformMakerTexture> textures = new();
    public bool HasChanges => clothesModelId != 0 || armbandModelId != 0 || gloveModelId != 0 || textures.Count > 0;
}

[Serializable]
public sealed class UniformMakerTexture
{
    public UniformPartKind part;
    public int channel, width, height, mipCount;
    public string originalName, fileName, extension, bytesBase64;
}
