using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using G4Runtime;
using UnityEngine;

namespace G4Composition
{
    [Serializable]
    public sealed class AvatarRecipeData
    {
        public string Code;
        public string VerificationCode;
        public string DecodeLayout;
        public List<AvatarRecipeValue> Values = new List<AvatarRecipeValue>();

        public AvatarRecipeValue Find(int category, int categoryParam, int categoryParamSub = 0)
        {
            return Values.FirstOrDefault(value => value.Category == category &&
                value.CategoryParam == categoryParam && value.CategoryParamSub == categoryParamSub);
        }
    }

    [Serializable]
    public sealed class AvatarRecipeValue
    {
        public int Index;
        public uint Id;
        public int Type;
        public uint Num;
        public int BitCount;
        public int Category;
        public int CategoryParam;
        public int CategoryParamSub;
        public uint RawValue;
    }

    [Serializable]
    public sealed class AvatarHeadParts
    {
        public string HeadModel;
        public string FaceTexture;
        public string EyeTexture;
        public string PupilTexture;
        public string HighlightTexture;
        public string EyebrowTexture;
        public string MouthTexture;
        public string HairFront;
        public string HairBack;
        public string HairUpper;
        public string Ear;
        public string Accessory;
    }

    public sealed class AvatarBoneTransform
    {
        public Vector3 Position;
        public Vector3 Rotation; // Local Euler angles in radians.
        public Vector3 Scale = Vector3.one;
    }

    public sealed class AvatarTextureTransform
    {
        public Vector2 Position;
        public float Scale = 1f;
        public float Rotation;
        // Eye height translation is in model units, rather than texture units.
        public float ModelHeight;
    }
    public static partial class AvatarRecipeParser
    {
        const string TablePrefix = "chara_edit_";
        const string CodeList = "m_CharaEditCodeInfoList";
        const string RecipeList = "m_CharaEditRecipeInfoList";

        static string BodySuffix(AvatarRecipeData recipe)
        {
            string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string path = Directory.GetFiles(directory, TablePrefix + "*.cfg.bin")
                .Single(candidate => !Path.GetFileName(candidate).StartsWith("chara_edit_parts_", StringComparison.OrdinalIgnoreCase));
            var table = new Rdbnp(File.ReadAllBytes(path), path);
            var list = table.GetList("m_CharaEditPartsDataList");
            uint selected = recipe.Find(1, 2)?.RawValue ?? 0u;
            uint gender = (recipe.Find(0, 0)?.RawValue ?? 0u) + 1u;
            for (int i = 0; i < list.Count; i++)
            {
                int row = list.Row(i);
                if (table.Byte(row + list.Field("itemNo")) != selected || table.U32(row + list.Field("gender")) != gender) continue;
                string name = table.String(row + list.Field("resourceNameStr1"));
                if (!name.StartsWith("edit_body_", StringComparison.Ordinal)) continue;
                string suffix = name.Substring("edit_body_".Length);
                // Female variants share the small, tall and big skeleton families.
                return suffix == "female" ? suffix : suffix.Replace("female", "");
            }
            throw new InvalidDataException("Unknown Avatar Recipe body type " + selected + " for style " + gender + ".");
        }
        public static AvatarBoneTransform ResolveHeadAttachment(AvatarRecipeData recipe, int slot = 0)
        {
            string suffix = BodySuffix(recipe);
            string body = suffix == "small" || suffix == "smallfat" ? "c000201_edit" :
                suffix == "tall" || suffix == "tallmuscle" ? "c000301_edit" :
                suffix == "muscle" || suffix == "big" ? "c000401_edit" : "c000101_edit";
            string path = Path.Combine(DumpDataManager.CommonPath, "chr", "_face", "20_EDIT", "_bodySK", body, body + ".objbin");
            var table = CfgTable.Read(File.ReadAllBytes(path), path);
            var result = new AvatarBoneTransform();
            bool active = false, found = false;
            foreach (var row in table.Rows)
            {
                if (row.Name != "SETUP_PARAM" || row.Values.Length < 2) continue;
                string parameter = row.Text(0);
                if (parameter == "meshAttachOffset")
                {
                    if (active) break;
                    active = row.Int(1) == slot;
                    found |= active;
                }
                if (!active) continue;
                float V(int i) => Convert.ToSingle(row.Values[i]);
                if (parameter == "meshAttachOffset_pos") result.Position = new Vector3(V(1), V(2), -V(3));
                if (parameter == "meshAttachOffset_rot") result.Rotation = new Vector3(-V(1), -V(2), V(3));
                if (parameter == "meshAttachOffset_scale") result.Scale = row.Values.Length == 2
                    ? Vector3.one * V(1) : new Vector3(V(1), V(2), V(3));
            }
            if (!found) throw new InvalidDataException("Missing mesh attachment slot " + slot + " in " + path);
            return result;
        }
        static string ResolveBaseFaceModel(AvatarRecipeData recipe, string directory)
        {
            string path = Directory.GetFiles(directory, "chara_edit_parts_type_config_*.cfg.bin").Single();
            var table = new Rdbnp(File.ReadAllBytes(path), path);
            var types = table.GetList("m_CharaEditFaceTypeInfoList");
            var faces = table.GetList("m_CharaEditFaceTypeDataList");
            uint faceId = G4TextureParser.Crc32("face_mdl_type_" + (recipe.Find(1, 8)?.RawValue ?? 1u).ToString("D2"));
            uint noseId = G4TextureParser.Crc32("nose_type_" + (recipe.Find(1, 6)?.RawValue ?? 1u).ToString("D2"));
            for (int i = 0; i < types.Count; i++)
            {
                int type = types.Row(i);
                if (table.U32(type + types.Field("faceTypeCrc")) != faceId) continue;
                uint span = table.U32(type + types.Field("faceTypeData"));
                int first = (int)(span & 0xFFFF), count = (int)(span >> 16);
                for (int j = first; j < first + count; j++)
                {
                    int row = faces.Row(j);
                    if (table.U32(row + faces.Field("noseTypeCrc")) != noseId) continue;
                    return table.String(row + faces.Field("resource_" + BodySuffix(recipe)));
                }
            }
            throw new InvalidDataException("No edit face model matches the recipe's body, face and nose types.");
        }

        public static Dictionary<int, AvatarTextureTransform> ResolveTextureTransforms(AvatarRecipeData recipe)
        {
            string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string path = Directory.GetFiles(directory, TablePrefix + "*.cfg.bin")
                .Single(candidate => !Path.GetFileName(candidate).StartsWith("chara_edit_parts_", StringComparison.OrdinalIgnoreCase));
            var table = new Rdbnp(File.ReadAllBytes(path), path);
            var list = table.GetList("m_CharaEditPartsParamDataList");
            int id = list.Field("partsID"), type = list.Field("paramType"), resource = list.Field("resourceName");
            int def = list.Field("paramDefault"), min = list.Field("paramMin"), max = list.Field("paramMax");
            // partsID is the table's opaque key, not the CRC of a filename.
            const uint common = 0xE5EC7051u;
            string bodySuffix = BodySuffix(recipe);
            string bodyFlag = "isApply" + char.ToUpperInvariant(bodySuffix[0]) + bodySuffix.Substring(1);
            int flag = list.Field(bodyFlag);
            string[] flags = { "isApplyMale", "isApplyFemale", "isApplySmall", "isApplySmallfat",
                "isApplyTall", "isApplyTallmuscle", "isApplyMuscle", "isApplyBig" };
            bool Unrestricted(int row) => flags.All(name => table.Byte(row + list.Field(name)) == 0);
            float Read(int row, uint slider)
            {
                float t = Math.Min(14u, slider) / 7f;
                float middle = table.Float(row + def);
                return t <= 1f ? table.Float(row + min) + (middle - table.Float(row + min)) * t
                    : middle + (table.Float(row + max) - middle) * (t - 1f);
            }
            uint Slider(int category, int parameter, int sub) => recipe.Find(category, parameter, sub)?.RawValue ?? 7u;
            var result = Enumerable.Range(1, 5).ToDictionary(parameter => parameter, parameter => new AvatarTextureTransform());
            int unflaggedGroup = 0;
            for (int i = 0; i < list.Count; i++)
            {
                int row = list.Row(i);
                if (table.U32(row + id) != common) continue;
                uint kind = table.U32(row + type), node = table.U32(row + resource);
                if (node == 0 && kind == 0 && i + 3 < list.Count)
                {
                    bool unrestricted = Unrestricted(row);
                    int parameter = unrestricted ? ++unflaggedGroup + 1 : 4;
                    if ((!unrestricted && table.Byte(row + flag) == 0) || parameter > 4) continue;
                    // Unrestricted groups are pupil then highlight. Eyebrows
                    // have one 0/1/3/8 block for each body family.
                    var change = result[parameter];
                    change.Position.x = Read(row, Slider(6, parameter, 0));
                    change.Position.y = Read(list.Row(i + 1), Slider(6, parameter, 1));
                    change.Scale = Read(list.Row(i + 2), Slider(6, parameter, 3));
                    change.Rotation = Read(list.Row(i + 3), Slider(6, parameter, 8));
                }
                else if (node == 0 && kind == 3 && table.Byte(row + flag) != 0 &&
                    i + 1 < list.Count && table.U32(list.Row(i + 1) + type) == 8u &&
                    (i == 0 || table.U32(list.Row(i - 1) + resource) != 0))
                {
                    // Only the eye block begins with scale/rotation.
                    result[1].Scale = Read(row, Slider(6, 1, 3));
                    result[1].Rotation = Read(list.Row(i + 1), Slider(6, 1, 8));
                }
                else if (node == 0x1A071132u && table.Byte(row + flag) != 0)
                {
                    if (kind == 1) result[1].ModelHeight = Read(row, Slider(3, 5, 1)) - table.Float(row + def);
                    if (kind == 8) result[1].Position.x = Read(row, Slider(3, 5, 8)) - table.Float(row + def);
                }
                else if (node == 0xC8F533FDu && kind == 7 && table.Byte(row + flag) != 0 && i + 1 < list.Count)
                {
                    // Mouth height is applied to its SP node, not the texture.
                    result[5].Scale = Read(list.Row(i + 1), Slider(6, 5, 3));
                }
            }
            return result;
        }


        /// <summary>Extract the complete block-encoded character number from grid OCR.</summary>
        public static string ExtractImageCode(IEnumerable<string> recognizedCandidates)
        {
            if (!DumpDataManager.ValidateSavedPath()) throw new InvalidDataException("Invalid Dump path: " + DumpDataManager.LastError);
            string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string path = Directory.GetFiles(directory, TablePrefix + "*.cfg.bin")
                .Single(candidate => !Path.GetFileName(candidate).StartsWith("chara_edit_parts_", StringComparison.OrdinalIgnoreCase));
            var table = new Rdbnp(File.ReadAllBytes(path), path);
            var alphabet = ReadAlphabet(table);
            int expected = ((6 + ReadDefinitions(table).Sum(value => value.BitCount) + 32 + 63) / 64) * 11;
            var valid = new HashSet<string>(StringComparer.Ordinal);
            foreach (string text in recognizedCandidates ?? Enumerable.Empty<string>())
            {
                if (text == null) continue;
                string code = new string(text.Normalize(NormalizationForm.FormKC).Where(value => !char.IsWhiteSpace(value)).ToArray());
                if (code.Length == expected && code.All(alphabet.ContainsKey)) valid.Add(code);
            }
            if (valid.Count == 0) throw new InvalidDataException("無法完整辨識 " + expected + " 碼角色編號。請使用完整的角色編號視窗截圖，或從 Recognized Text 查看辨識結果。");
            if (valid.Count > 1) throw new InvalidDataException("兩次辨識的角色編號不一致。請從 Recognized Text 查看並手動確認，避免建立錯誤角色。");
            return valid.Single();
        }
        public static AvatarRecipeData Parse(string code)
        {
            if (!DumpDataManager.ValidateSavedPath())
                throw new InvalidDataException("Invalid Dump path: " + DumpDataManager.LastError);
            string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string[] candidates = Directory.GetFiles(directory, TablePrefix + "*.cfg.bin")
                .Where(path => Path.GetFileName(path).StartsWith(TablePrefix, StringComparison.OrdinalIgnoreCase) &&
                    !Path.GetFileName(path).StartsWith("chara_edit_parts_", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException("Expected exactly one chara_edit_*.cfg.bin file.");
            return Parse(code, File.ReadAllBytes(candidates[0]), candidates[0]);
        }

        public static Dictionary<uint, AvatarBoneTransform> ResolveFaceBoneTransforms(AvatarRecipeData recipe)
        {
            string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string path = Directory.GetFiles(directory, TablePrefix + "*.cfg.bin")
                .Single(candidate => !Path.GetFileName(candidate).StartsWith("chara_edit_parts_", StringComparison.OrdinalIgnoreCase));
            var table = new Rdbnp(File.ReadAllBytes(path), path);
            var list = table.GetList("m_CharaEditPartsParamDataList");
            uint faceType = Math.Max(1u, Math.Min(6u, recipe.Find(1, 8)?.RawValue ?? 1u));
            uint partId = G4TextureParser.Crc32("face_mdl_type_" + faceType.ToString("D2"));
            if (!Enumerable.Range(0, list.Count).Any(i => table.U32(list.Row(i) + list.Field("partsID")) == partId))
                partId = 0xE5EC7051u; // Shared face parameters when no type-specific block exists.
            float faceWidth = Math.Min(14u, recipe.Find(3, 0)?.RawValue ?? 7u);
            float jawWidth = Math.Min(14u, recipe.Find(3, 1)?.RawValue ?? 7u);
            float faceLength = Math.Min(14u, recipe.Find(3, 2, 1)?.RawValue ?? 7u);
            uint leftCheek = G4TextureParser.Crc32("l_chk1_1_0"), rightCheek = G4TextureParser.Crc32("r_chk1_1_0");
            uint leftJaw = G4TextureParser.Crc32("l_jwl_1_0"), rightJaw = G4TextureParser.Crc32("r_jwl_1_0");
            uint centerJaw = G4TextureParser.Crc32("c_jwl_1_0");
            var result = new Dictionary<uint, AvatarBoneTransform>();
            int id = list.Field("partsID"), type = list.Field("paramType"), def = list.Field("paramDefault");
            int min = list.Field("paramMin"), max = list.Field("paramMax"), resource = list.Field("resourceName");
            for (int i = 0; i < list.Count; i++)
            {
                int row = list.Row(i);
                if (table.U32(row + id) != partId) continue;
                uint bone = table.U32(row + resource);
                float slider = bone == leftCheek || bone == rightCheek ? faceWidth :
                    bone == leftJaw || bone == rightJaw ? jawWidth : bone == centerJaw ? faceLength : -1f;
                if (slider < 0f) continue;
                float middle = table.Float(row + def);
                uint parameterType = table.U32(row + type);
                // Zero endpoints on unused scale channels are placeholders,
                // not a request to collapse the bone as a width slider moves.
                if (parameterType >= 4 && parameterType <= 6 &&
                    table.Float(row + min) == 0f && table.Float(row + max) == 0f) continue;
                float value = slider <= 7f ? Mathf.Lerp(table.Float(row + min), middle, slider / 7f) :
                    Mathf.Lerp(middle, table.Float(row + max), (slider - 7f) / 7f);
                if (!result.TryGetValue(bone, out AvatarBoneTransform transform))
                    result.Add(bone, transform = new AvatarBoneTransform());
                switch (table.U32(row + type))
                {
                    case 0: transform.Position.x = value; break;
                    case 1: transform.Position.y = value; break;
                    case 2: transform.Position.z = -value; break;
                    case 4: transform.Scale.x = value; break;
                    case 5: transform.Scale.y = value; break;
                    case 6: transform.Scale.z = value; break;
                }
            }

            // Nose height is stored in the type-01/common parameter block. It
            // has one Y-translation row for each face-base skeleton family.
            uint noseBone = 0xC301503Cu;
            uint commonPartId = 0xE5EC7051u;
            float noseHeight = Math.Min(6u, recipe.Find(3, 6, 1)?.RawValue ?? 3u);
            string bodyFamily = BodySuffix(recipe);
            int familyRow = bodyFamily == "small" || bodyFamily == "smallfat" ? 1 :
                bodyFamily == "tall" || bodyFamily == "tallmuscle" ? 2 :
                bodyFamily == "muscle" || bodyFamily == "big" ? 3 : 0;
            int matchingRow = 0;
            for (int i = 0; i < list.Count; i++)
            {
                int row = list.Row(i);
                if (table.U32(row + id) != commonPartId || table.U32(row + resource) != noseBone || table.U32(row + type) != 1u)
                    continue;
                if (matchingRow++ != familyRow) continue;
                float middle = table.Float(row + def);
                float value = noseHeight <= 3f ? Mathf.Lerp(table.Float(row + min), middle, noseHeight / 3f) :
                    Mathf.Lerp(middle, table.Float(row + max), (noseHeight - 3f) / 3f);
                if (!result.TryGetValue(noseBone, out AvatarBoneTransform transform))
                    result.Add(noseBone, transform = new AvatarBoneTransform());
                transform.Position.y = value;
                break;
            }
            // Mouth height rotates its SP node around local X. Its pivot is
            // behind the face; radians must not be converted as a Y distance.
            string suffix = BodySuffix(recipe);
            int mouthBodyFlag = list.Field("isApply" + char.ToUpperInvariant(suffix[0]) + suffix.Substring(1));
            for (int i = 0; i < list.Count; i++)
            {
                int row = list.Row(i);
                if (table.U32(row + id) != commonPartId || table.U32(row + resource) != 0xC8F533FDu ||
                    table.U32(row + type) != 7u || table.Byte(row + mouthBodyFlag) == 0) continue;
                float slider = Math.Min(14u, recipe.Find(3, 4, 7)?.RawValue ?? 7u);
                float middle = table.Float(row + def);
                float angle = slider <= 7f ? Mathf.Lerp(table.Float(row + min), middle, slider / 7f) :
                    Mathf.Lerp(middle, table.Float(row + max), (slider - 7f) / 7f);
                result[0xC8F533FDu] = new AvatarBoneTransform { Rotation = new Vector3(angle, 0f, 0f) };
                break;
            }
            return result;
        }
        public static Dictionary<uint, AvatarBoneTransform> ResolveEarBoneTransforms(AvatarRecipeData recipe, string earModel)
        {
            string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string path = Directory.GetFiles(directory, TablePrefix + "*.cfg.bin")
                .Single(candidate => !Path.GetFileName(candidate).StartsWith("chara_edit_parts_", StringComparison.OrdinalIgnoreCase));
            var table = new Rdbnp(File.ReadAllBytes(path), path);
            var list = table.GetList("m_CharaEditPartsParamDataList");
            const uint commonPart = 0xE5EC7051u;
            uint left = G4TextureParser.Crc32("l_ear1_1_0"), right = G4TextureParser.Crc32("r_ear1_1_0");
            float scaleSlider = Math.Min(6u, recipe.Find(3, 3, 3)?.RawValue ?? 3u);
            float heightSlider = Math.Min(6u, recipe.Find(3, 3, 1)?.RawValue ?? 3u);
            string suffix = BodySuffix(recipe);
            int bodyFlag = list.Field("isApply" + char.ToUpperInvariant(suffix[0]) + suffix.Substring(1));
            string[] bodyFlags = { "isApplyMale", "isApplyFemale", "isApplySmall", "isApplySmallfat",
                "isApplyTall", "isApplyTallmuscle", "isApplyMuscle", "isApplyBig" };
            var result = new Dictionary<uint, AvatarBoneTransform>();
            int id = list.Field("partsID"), type = list.Field("paramType"), def = list.Field("paramDefault");
            int min = list.Field("paramMin"), max = list.Field("paramMax"), resource = list.Field("resourceName");
            for (int i = 0; i < list.Count; i++)
            {
                int row = list.Row(i);
                uint bone = table.U32(row + resource), paramType = table.U32(row + type);
                if (table.U32(row + id) != commonPart || bone != left && bone != right || paramType != 1u && paramType != 3u)
                    continue;
                // Large-body ears use a separate 1.8x base scale. These rows
                // are selected by body flags, not by the ear model number.
                bool restricted = bodyFlags.Any(name => table.Byte(row + list.Field(name)) != 0);
                bool largeBody = suffix == "muscle" || suffix == "big";
                if (restricted ? table.Byte(row + bodyFlag) == 0 : largeBody) continue;
                float slider = paramType == 1u ? heightSlider : scaleSlider;
                float middle = table.Float(row + def);
                float value = slider <= 3f ? Mathf.Lerp(table.Float(row + min), middle, slider / 3f) :
                    Mathf.Lerp(middle, table.Float(row + max), (slider - 3f) / 3f);
                if (!result.TryGetValue(bone, out AvatarBoneTransform transform))
                    result.Add(bone, transform = new AvatarBoneTransform());
                if (paramType == 1u) transform.Position.y = value;
                else transform.Scale = Vector3.one * value;
            }
            return result;
        }
        public static AvatarHeadParts ResolveHeadParts(AvatarRecipeData recipe)
        {
            if (recipe == null) throw new ArgumentNullException(nameof(recipe));
            string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string path = Directory.GetFiles(directory, TablePrefix + "*.cfg.bin")
                .Single(candidate => !Path.GetFileName(candidate).StartsWith("chara_edit_parts_", StringComparison.OrdinalIgnoreCase));
            var table = new Rdbnp(File.ReadAllBytes(path), path);
            Rdbnp.List list = table.GetList("m_CharaEditPartsDataList");
            int itemNo = list.Field("itemNo");
            int name1 = list.Field("resourceNameStr1"), name2 = list.Field("resourceNameStr2");

            string Find(uint selected, string prefix, int nameField)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    int row = list.Row(i);
                    if (table.Byte(row + itemNo) != selected) continue;
                    string name = table.String(row + nameField);
                    if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return name;
                }
                return null;
            }

            string FindFront(uint selected)
            {
                for (int i = 0; i < list.Count; i++)
                {
                    int row = list.Row(i);
                    if (table.Byte(row + itemNo) != selected) continue;
                    string front = table.String(row + name1), paired = table.String(row + name2);
                    if (front.StartsWith("hairF", StringComparison.OrdinalIgnoreCase) && string.IsNullOrEmpty(paired)) return front;
                }
                return null;
            }
            uint hair = recipe.Find(1, 1)?.RawValue ?? 0;
            uint frontHair = recipe.Find(1, 0)?.RawValue ?? hair;
            uint face = recipe.Find(4, 0)?.RawValue ?? 0;
            uint nose = recipe.Find(1, 6)?.RawValue ?? 0;
            uint ear = recipe.Find(1, 5)?.RawValue ?? 1;
            uint accessory = recipe.Find(1, 12)?.RawValue ?? 0;
            return new AvatarHeadParts
            {
                // The face base and nose are one combined mesh; nose IDs are one based.
                HeadModel = ResolveBaseFaceModel(recipe, directory),
                FaceTexture = Find(face, "face_", name1) ?? "face_00",
                EyeTexture = Find(recipe.Find(4, 1)?.RawValue ?? 0, "eye_", name1),
                PupilTexture = Find(recipe.Find(4, 2)?.RawValue ?? 0, "pupil_", name1),
                HighlightTexture = Find(recipe.Find(4, 3)?.RawValue ?? 0, "highlight_", name1),
                EyebrowTexture = Find(recipe.Find(4, 4)?.RawValue ?? 0, "eyebrow_", name1),
                MouthTexture = Find(recipe.Find(4, 5)?.RawValue ?? 0, "mouth_", name1),
                HairFront = hair < 54 ? FindFront(frontHair) ?? FindFront(frontHair + 1) : null,
                HairBack = hair < 54 ? Find(hair, "hairB", name2) ?? Find(hair + 1, "hairB", name2) : null,
                HairUpper = hair >= 54 ? Find(hair, "hairU", name2) ?? Find(hair, "hairU", name1) ?? Find(hair + 1, "hairU", name2) ?? Find(hair + 1, "hairU", name1) : null,
                Ear = Find(ear, "ear", name1) ?? Find(ear + 1, "ear", name1),
                Accessory = accessory == 0 ? null : Find(accessory, "accessory", name1) ?? Find(accessory + 1, "accessory", name1)
            };
        }

        public static AvatarRecipeData Parse(string code, byte[] charaEditTable, string source = "chara_edit")
        {
            if (code == null) throw new ArgumentNullException(nameof(code));
            string normalized = new string(code.Where(value => !char.IsWhiteSpace(value)).ToArray());
            var table = new Rdbnp(charaEditTable, source);
            Dictionary<char, int> alphabet = ReadAlphabet(table);
            List<RecipeDefinition> definitions = ReadDefinitions(table);
            int dataBits = definitions.Sum(value => value.BitCount);
            // Eleven characters store 64 bits with two leading padding bits.
            // The entire packet is XORed bytewise with 0xAC.
            int blockCount = (6 + dataBits + 32 + 63) / 64;
            int expectedCharacters = blockCount * 11;
            if (normalized.Length != expectedCharacters)
                throw new InvalidDataException("Avatar Recipe must contain exactly " + expectedCharacters + " characters.");
            var bits = new List<int>(blockCount * 64);
            for (int block = 0; block < blockCount; block++)
            for (int character = 0; character < 11; character++)
            {
                char glyph = normalized[block * 11 + character];
                if (!alphabet.TryGetValue(glyph, out int word))
                    throw new InvalidDataException("Avatar Recipe contains an invalid character: " + glyph);
                if (character == 0 && word >= 16)
                    throw new InvalidDataException("Avatar Recipe has invalid block padding at character " + (block * 11 + 1) + ".");
                for (int shift = character == 0 ? 3 : 5; shift >= 0; shift--)
                {
                    int mask = (0xAC >> (7 - bits.Count % 8)) & 1;
                    bits.Add(((word >> shift) & 1) ^ mask);
                }
            }
            int cursor = 0;
            uint Read(int count)
            {
                uint value = 0;
                for (int bit = 0; bit < count; bit++) value = (value << 1) | (uint)bits[cursor++];
                return value;
            }
            uint header = Read(6);
            if (header != 0) throw new InvalidDataException("Unsupported Avatar Recipe header: " + header);
            var decoded = definitions.Select(definition => Read(definition.BitCount)).ToList();
            var result = new AvatarRecipeData
            {
                Code = normalized,
                DecodeLayout = "64-bit-blocks/msb/xor-ac",
                VerificationCode = Read(32).ToString("X8")
            };
            for (int index = 0; index < definitions.Count; index++)
            {
                RecipeDefinition definition = definitions[index];
                result.Values.Add(new AvatarRecipeValue
                {
                    Index = index,
                    Id = definition.Id,
                    Type = definition.Type,
                    Num = definition.Num,
                    BitCount = definition.BitCount,
                    Category = definition.Category,
                    CategoryParam = definition.CategoryParam,
                    CategoryParamSub = definition.CategoryParamSub,
                    RawValue = decoded[index]
                });
            }
            return result;
        }

        static Dictionary<char, int> ReadAlphabet(Rdbnp table)
        {
            Rdbnp.List list = table.GetList(CodeList);
            int num = list.Field("num"), character = list.Field("codeChar");
            var result = new Dictionary<char, int>();
            for (int i = 0; i < list.Count; i++)
            {
                int row = list.Row(i);
                int value = checked((int)table.U32(row + num));
                int stringOffset = table.Int(row + character);
                char code = table.UnicodeCharacter(stringOffset);
                if (value < 0 || value > 63 || result.ContainsKey(code))
                    throw new InvalidDataException(table.Source + ": invalid Avatar Recipe alphabet.");
                result.Add(code, value);
            }
            if (result.Count != 64) throw new InvalidDataException(table.Source + ": Avatar Recipe alphabet must contain 64 characters.");
            return result;
        }

        static List<RecipeDefinition> ReadDefinitions(Rdbnp table)
        {
            Rdbnp.List list = table.GetList(RecipeList);
            int id = list.Field("id"), type = list.Field("type"), num = list.Field("num");
            int bitNum = list.Field("bitNum"), category = list.Field("category");
            int categoryParam = list.Field("categoryParam"), categoryParamSub = list.Field("categoryParamSub");
            var result = new List<RecipeDefinition>();
            for (int i = 0; i < list.Count; i++)
            {
                int row = list.Row(i); int width = table.Byte(row + bitNum);
                if (width < 1 || width > 32) throw new InvalidDataException(table.Source + ": invalid Avatar Recipe bit count.");
                result.Add(new RecipeDefinition
                {
                    Id = table.U32(row + id), Type = table.Byte(row + type), Num = table.U32(row + num),
                    BitCount = width, Category = table.Byte(row + category),
                    CategoryParam = table.Byte(row + categoryParam), CategoryParamSub = table.Byte(row + categoryParamSub)
                });
            }
            return result;
        }

        sealed class RecipeDefinition
        {
            public uint Id, Num;
            public int Type, BitCount, Category, CategoryParam, CategoryParamSub;
        }

        internal sealed class Rdbnp
        {
            readonly byte[] data;
            readonly G4ModelParser.Binary binary;
            readonly int types, fields, roots, values, strings, typeCount, fieldCount, rootCount;
            readonly Dictionary<uint, string> names = new Dictionary<uint, string>();
            public string Source { get; }

            public Rdbnp(byte[] bytes, string source)
            {
                data = bytes ?? throw new ArgumentNullException(nameof(bytes)); Source = source;
                binary = new G4ModelParser.Binary(data, source); binary.Magic("RDBNP");
                int baseline = checked(binary.U16(10) * 4);
                Func<int, int> section = field => checked(baseline + binary.U16(field) * 4);
                types = section(0x24); fields = section(0x28); roots = section(0x2c);
                int hashes = section(0x30), offsets = section(0x32); values = section(0x36);
                strings = checked(baseline + binary.Int(0x38));
                typeCount = binary.U16(0x26); fieldCount = binary.U16(0x2a); rootCount = binary.U16(0x2e);
                int hashCount = binary.U16(0x34);
                binary.Range(types, typeCount * 32L); binary.Range(fields, fieldCount * 32L);
                binary.Range(roots, rootCount * 32L); binary.Range(hashes, hashCount * 4L);
                binary.Range(offsets, hashCount * 4L);
                for (int i = 0; i < hashCount; i++)
                    names[binary.U32(hashes + i * 4)] = binary.Text(checked(strings + binary.Int(offsets + i * 4)));
            }

            public List GetList(string listName)
            {
                for (int i = 0; i < rootCount; i++)
                {
                    int root = roots + i * 32;
                    if (!names.TryGetValue(binary.U32(root + 16), out string name) || name != listName) continue;
                    int type = binary.U16(root);
                    if (type < 0 || type >= typeCount) throw new InvalidDataException(Source + ": invalid RDBNP type.");
                    int first = binary.U16(types + type * 32 + 8), count = binary.U16(types + type * 32 + 10);
                    if (first + count > fieldCount) throw new InvalidDataException(Source + ": invalid RDBNP fields.");
                    int stride = binary.Int(root + 8), rows = checked(values + binary.Int(root + 4)), rowCount = binary.Int(root + 12);
                    binary.Range(rows, rowCount * (long)stride);
                    var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
                    for (int fieldIndex = first; fieldIndex < first + count; fieldIndex++)
                    {
                        int field = fields + fieldIndex * 32;
                        if (names.TryGetValue(binary.U32(field), out string fieldName)) offsets[fieldName] = binary.Int(field + 12);
                    }
                    return new List(Source, rows, rowCount, stride, offsets);
                }
                throw new InvalidDataException(Source + ": missing " + listName + ".");
            }

            public int Byte(int offset) { binary.Range(offset, 1); return data[offset]; }
            public int Int(int offset) => binary.Int(offset);
            public uint U32(int offset) => binary.U32(offset);
            public float Float(int offset) => binary.F32(offset);
            public string String(int offset)
            {
                // RDBNP string references are signed relative offsets. Binary.Int
                // intentionally rejects values with the high bit set because most
                // G4 integer fields are unsigned sizes, so read this field directly.
                binary.Range(offset, 4);
                int relative = BitConverter.ToInt32(data, offset);
                return relative == 0 ? string.Empty : binary.Text(checked(strings + relative));
            }
            public char UnicodeCharacter(int relativeOffset)
            {
                int offset = checked(strings + relativeOffset); binary.Range(offset, 2);
                return BitConverter.ToChar(data, offset);
            }

            public sealed class List
            {
                readonly string source; readonly int rows, stride;
                readonly Dictionary<string, int> fields;
                public int Count { get; }
                public List(string source, int rows, int count, int stride, Dictionary<string, int> fields)
                { this.source = source; this.rows = rows; Count = count; this.stride = stride; this.fields = fields; }
                public int Row(int index) => index >= 0 && index < Count ? checked(rows + index * stride) : throw new ArgumentOutOfRangeException(nameof(index));
                public int Field(string name) => fields.TryGetValue(name, out int offset) ? offset : throw new InvalidDataException(source + ": missing field " + name + ".");
                public string[] FieldNames => fields.Keys.ToArray();
            }
        }
    }
}

