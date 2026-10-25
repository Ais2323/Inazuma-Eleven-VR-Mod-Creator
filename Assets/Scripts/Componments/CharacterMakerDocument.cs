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
    public CharacterIdentitySettings identity;
    public string voiceCharacterId, voiceAvatarCode, voiceName, voicePortraitPng;
    public string avatarCode;
    public string avatarInput;
    public CharacterMakerPart head = new();
    public CharacterMakerPart hair = new();
    public CharacterMakerPart other = new();
    public int bodyType = 1;
    public uint bodyVariantId;
    public string bodyAvatarCode;
    public int chestSize = -1;
    public int uniformCollar;
    public int equipmentFlag2;
    public int uniformSleeve, uniformHem;
    public int Hem => version >= 6 ? uniformHem : equipmentFlag2;
    public Color skinColor = Color.white;
    public List<CharacterMakerTexture> textures = new();
    public string portraitG4txBase64;
    public string iconFrontPng, iconBackPng;

    public void NormalizeBodyGender(G4Composition.CfgTable models, int gender, G4Composition.CfgTable.Row original)
    {
        if (version < 9 && gender == 1 && chestSize == 0) chestSize = -1;
        var body = G4Composition.NativeBodyTypes.ResolveForGender(models, bodyType, bodyVariantId, gender, original);
        if (body == null) throw new InvalidDataException("找不到符合性別的體型。");
        if (bodyVariantId != body.UInt(0)) bodyAvatarCode = null;
        bodyVariantId = body.UInt(0); bodyType = G4Composition.NativeBodyTypes.FromRow(body);
    }

    public void UpgradeBodySettings(G4Composition.CfgTable models)
    {
        if (version >= 8) return;
        G4Composition.CfgTable.Row body;
        if (!string.IsNullOrWhiteSpace(bodyAvatarCode))
        {
            // Re-resolve an applied Avatar recipe with the corrected native mesh mapping.
            // Manually selected shapes clear bodyAvatarCode and retain their exact variant.
            var recipe = G4Composition.AvatarRecipeParser.Parse(bodyAvatarCode);
            var applied = G4Composition.AvatarRecipeParser.ResolveBody(recipe, models);
            body = models.Rows.Find(r => G4Composition.NativeBodyTypes.IsBody(r) && r.UInt(0) == applied.VariantId);
        }
        else if (bodyVariantId != 0)
            body = models.Rows.Find(r => G4Composition.NativeBodyTypes.IsBody(r) && r.UInt(0) == bodyVariantId);
        else if (version >= 7)
            body = G4Composition.NativeBodyTypes.Resolve(models, bodyType, 0, bodyType == 2);
        else
        {
            // The old group 4 combines large builds; its default must be the round build.
            body = bodyType == 4 ? models.Rows.Find(r => G4Composition.NativeBodyTypes.IsBody(r) && r.Int(5) == 3 && r.Int(6) == 6) : null;
            body ??= models.Rows.Find(r => G4Composition.NativeBodyTypes.IsBody(r) && r.Int(5) == bodyType - 1);
        }
        if (body == null) throw new InvalidDataException("無法轉換舊存檔的體型設定。");
        uniformHem = Hem;
        bodyType = G4Composition.NativeBodyTypes.FromRow(body);
        bodyVariantId = body.UInt(0);
        version = 8;
    }

    public static CharacterMakerDocument Read(string path)
    {
        CharacterMakerDocument document = JsonUtility.FromJson<CharacterMakerDocument>(File.ReadAllText(path));
        if (document == null || document.version < 1 || document.version > 11)
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
    public string avatarCode;
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
    public int avatarAtlasVersion;
}
