using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class CharacterMakerDocument
{
    public int version;
    public string targetId;
    public CharacterMakerPart head = new();
    public CharacterMakerPart hair = new();
    public CharacterMakerPart other = new();
    public int bodyType = 1;
    public int chestSize;
    public int uniformCollar;
    public int equipmentFlag2;
    public Color skinColor = Color.white;
    public List<CharacterMakerTexture> textures = new();
    public string portraitG4txBase64;

    public static CharacterMakerDocument Read(string path)
    {
        CharacterMakerDocument document = JsonUtility.FromJson<CharacterMakerDocument>(File.ReadAllText(path));
        if (document == null || document.version < 1 || document.version > 2)
            throw new InvalidDataException("不支援的 CharacterMaker 存檔版本。");
        document.head ??= new CharacterMakerPart();
        document.hair ??= new CharacterMakerPart();
        document.other ??= new CharacterMakerPart();
        document.textures ??= new List<CharacterMakerTexture>();
        return document;
    }

    public void Write(string path)
    {
        File.WriteAllText(path, JsonUtility.ToJson(this, true), new UTF8Encoding(false));
    }
}

[Serializable]
public sealed class CharacterMakerPart
{
    public string sourceId;
    public Vector3 position;
    public Vector3 rotation;
    public Vector3 scale = Vector3.one;
    public Vector3 exportPosition;
    public List<string> meshNames = new();
}

[Serializable]
public sealed class CharacterMakerTexture
{
    public int option;
    public string sourceId;
    public string role;
    public string ddsBase64;
}
