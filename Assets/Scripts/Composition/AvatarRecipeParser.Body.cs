using System;
using System.IO;
using System.Linq;
using UnityEngine;

namespace G4Composition
{
    public sealed class AvatarBodySettings
    {
        public string Code, Resource;
        public int BodyType, ChestSize, Collar, Sleeve, Hem;
        public uint VariantId;
    }

    public static partial class AvatarRecipeParser
    {
        public static AvatarBodySettings ResolveBody(AvatarRecipeData recipe, CfgTable models)
        {
            string suffix = BodySuffix(recipe);
            bool female = recipe.Find(0, 0)?.RawValue == 1;
            string family = suffix.Replace("female", "");
            int shape = family == "small" ? 2 : family == "smallfat" ? 3 : family == "tall" ? 4 :
                family == "tallmuscle" ? 7 : family == "muscle" ? 5 : family == "big" ? 6 : female ? 1 : 0;
            int group = shape == 2 || shape == 3 ? (female ? 5 : 1) :
                shape == 4 || shape == 7 ? (female ? 6 : 2) :
                shape == 5 || shape == 6 ? (female ? 7 : 3) : female ? 4 : 0;
            var body = models.Rows.FirstOrDefault(r => r.Name == "CHARA_BODY_INFO" && r.Values.Length >= 7 &&
                r.Int(5) == group && r.Int(6) == shape && r.Text(2)?.StartsWith("_uniform/sh", StringComparison.OrdinalIgnoreCase) == true);
            if (body == null) throw new InvalidDataException("Missing Avatar body variant: " + suffix);
            return new AvatarBodySettings { Code = recipe.Code, Resource = "edit_body_" + suffix,
                BodyType = NativeBodyTypes.FromRow(body), VariantId = body.UInt(0),
                Collar = (int)(recipe.Find(7, 1, 1)?.RawValue ?? 0),
                Sleeve = (int)(recipe.Find(7, 1, 2)?.RawValue ?? 0),
                Hem = (int)(recipe.Find(7, 1, 3)?.RawValue ?? 0),
                ChestSize = female ? checked((int)(recipe.Find(1, 10)?.RawValue ?? 0)) : -1 };
        }
        public static string ResolveVoiceName(AvatarRecipeData recipe)
        {
            string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string path = Directory.GetFiles(directory, TablePrefix + "*.cfg.bin")
                .Single(candidate => !Path.GetFileName(candidate).StartsWith("chara_edit_parts_", StringComparison.OrdinalIgnoreCase));
            var table = new Rdbnp(File.ReadAllBytes(path), path);
            var voices = table.GetList("m_CharaEditVoiceInfoList");
            uint number = recipe.Find(9, 0)?.RawValue ?? 0;
            for (int i = 0; i < voices.Count; i++)
            {
                int row = voices.Row(i);
                if (table.Byte(row + voices.Field("itemNo")) == number)
                    return table.String(row + voices.Field("charaSeName"));
            }
            throw new InvalidDataException("Unknown Avatar voice: " + number);
        }

        public static string ResolveGenericVoiceName(string commonPath, uint id)
        {
            string directory = Path.Combine(commonPath, "gamedata", "character");
            string path = Directory.GetFiles(directory, "general_battle_voice_*.cfg.bin").Single();
            var table = new Rdbnp(File.ReadAllBytes(path), path);
            var voices = table.GetList("m_generalBattleVoice");
            for (int i = 0; i < voices.Count; i++)
            {
                int row = voices.Row(i);
                if (table.U32(row + voices.Field("voiceId")) == id)
                    return table.String(row + voices.Field("voiceIdString"));
            }
            return null;
        }

        static System.Collections.Generic.Dictionary<uint, System.Collections.Generic.HashSet<int>> fashionBodies;
        static string fashionBodyPath;

        public static bool IsFashionAllowed(CfgTable.Row body, int fashion)
        {
            string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string path = Directory.GetFiles(directory, TablePrefix + "*.cfg.bin")
                .Single(candidate => !Path.GetFileName(candidate).StartsWith("chara_edit_parts_", StringComparison.OrdinalIgnoreCase));
            if (fashionBodies == null || fashionBodyPath != path)
            {
                var table = new Rdbnp(File.ReadAllBytes(path), path);
                var infos = table.GetList("m_CharaEditFashionBodyInfoList");
                var bodies = table.GetList("m_CharaEditFashionBodyDataList");
                var loaded = new System.Collections.Generic.Dictionary<uint, System.Collections.Generic.HashSet<int>>();
                for (int i = 0; i < infos.Count; i++)
                {
                    int row = infos.Row(i), tuple = row + infos.Field("enableBodyType");
                    uint span = table.U32(tuple);
                    int first = (int)(span & 0xffff), count = (int)(span >> 16);
                    var allowed = new System.Collections.Generic.HashSet<int>();
                    for (int j = 0; j < count; j++) allowed.Add(table.Byte(bodies.Row(first + j) + bodies.Field("bodyType")));
                    loaded.Add(table.U32(row + infos.Field("id")), allowed);
                }
                fashionBodies = loaded;
                fashionBodyPath = path;
            }
            uint id = fashion == 2 ? 0x215e4aeau : fashion == 3 ? 0x9502b1e7u : fashion == 4 ? 0xf0dc63c1u : 0xe24565a8u;
            return body != null && fashionBodies.TryGetValue(id, out var set) && set.Contains((int)body.Int(4));
        }
    }
}

namespace G4Composition
{
    public static class NativeUniformFashion
    {
        public sealed class Selection
        {
            public uint ModelId;
            public string Uniform, UniformTexture, Skin, SkinTexture;
        }
        static CfgTable table;
        static string tablePath;
        static CfgTable Load()
        {
            string dir = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
            string path = Directory.GetFiles(dir, "chara_parts_*.cfg.bin").Single();
            if (table == null || path != tablePath) { table = CfgTable.Read(File.ReadAllBytes(path), path); tablePath = path; }
            return table;
        }
        public static string ModelName(uint id)
        {
            return Load().Rows.FirstOrDefault(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.UInt(0) == id)?.Text(1);
        }
        public static Selection Resolve(uint id, CfgTable.Row body, int sleeve, int hem)
        {
            string name = ModelName(id);
            if (string.IsNullOrWhiteSpace(name)) return null;
            foreach (string suffix in new[] { "_slv", "_blyR" })
                if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - suffix.Length);
            if (sleeve != 0) name += "_slv";
            else if (hem == 2) name += "_blyR";
            return Resolve(name, body);
        }
        public static Selection Resolve(string name, CfgTable.Row body)
        {
            var cfg = Load();
            int index = cfg.Rows.FindIndex(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.Text(1) == name);
            if (index < 0) return null;
            var reference = cfg.Rows[index + 1];
            if (reference.Name != "CHARA_PARTS_CLOTHES_MODEL_REF_INFO") throw new InvalidDataException("Missing uniform reference: " + name);
            var rows = cfg.Rows.Where(r => r.Name == "CHARA_PARTS_CLOTHES_INFO").Skip((int)reference.Int(0)).Take((int)reference.Int(1)).ToArray();
            var selected = rows.FirstOrDefault(r => r.Int(2) == body.Int(4)) ?? rows.FirstOrDefault(r => r.Int(2) == body.Int(6));
            if (selected == null) return null;
            string Read(int field) => !string.IsNullOrWhiteSpace(selected.Text(field)) ? selected.Text(field) : rows[0].Text(field);
            return new Selection { ModelId = cfg.Rows[index].UInt(0), Uniform = Read(0), UniformTexture = Read(1), Skin = Read(6), SkinTexture = Read(7) };
        }
        public static bool Allowed(CfgTable.Row body, int sleeve, int hem)
        {
            return (sleeve == 0 || AvatarRecipeParser.IsFashionAllowed(body, 2)) &&
                (hem != 2 || AvatarRecipeParser.IsFashionAllowed(body, 3));
        }
        public static uint ExportModel(uint originalId, CfgTable.Row body, int sleeve, int hem)
        {
            if (!Allowed(body, sleeve, hem)) throw new InvalidDataException("The selected body does not support this sleeve or hem.");
            if (sleeve == 0 && hem != 2)
            {
                string original = ModelName(originalId);
                if (original == null || (!original.EndsWith("_slv", StringComparison.OrdinalIgnoreCase) && !original.EndsWith("_blyR", StringComparison.OrdinalIgnoreCase))) return originalId;
            }
            var selected = Resolve(originalId, body, sleeve, hem);
            if (selected == null) throw new InvalidDataException("This uniform does not support the selected sleeve or hem.");
            return selected.ModelId;
        }
    }
}

namespace G4Composition
{
    public static class NativeBodyTypes
    {
        // UI order: male, female, small, smallfat, tall, tallmuscle, big, muscle.
        // CHARA_BODY_INFO field 5 is a skeleton/gender group, not this shape index.
        static readonly int[] Shapes = { 0, 1, 2, 3, 4, 7, 6, 5 };
        public static bool IsBody(CfgTable.Row row) => row != null && row.Name == "CHARA_BODY_INFO" && row.Values.Length >= 7 &&
            row.Text(2)?.StartsWith("_uniform/sh", StringComparison.OrdinalIgnoreCase) == true;
        public static bool Matches(CfgTable.Row row, int type) => IsBody(row) && type >= 1 && type <= 8 && row.Int(6) == Shapes[type - 1];
        public static bool AllowsGender(CfgTable.Row row, int gender) => IsBody(row) &&
            (gender != 1 && gender != 2 || (row.Int(5) >= 4) == (gender == 2));
        public static bool CanSelect(CfgTable models, int type, int gender) =>
            models.Rows.Any(r => Matches(r, type) && AllowsGender(r, gender));
        public static CfgTable.Row ResolveForGender(CfgTable models, int type, uint variant, int gender, CfgTable.Row original = null)
        {
            // Only the two standard shapes are exclusive. All other shapes have
            // distinct male/female rows even when their physical mesh is shared.
            if (!CanSelect(models, type, gender)) type = gender == 2 ? 2 : 1;
            var exact = variant == 0 ? null : models.Rows.FirstOrDefault(r => Matches(r, type) && r.UInt(0) == variant);
            if (AllowsGender(exact, gender)) return exact;
            if (Matches(original, type) && AllowsGender(original, gender)) return original;
            return models.Rows.FirstOrDefault(r => Matches(r, type) && AllowsGender(r, gender));
        }
        public static int FromRow(CfgTable.Row row)
        {
            int type = Array.IndexOf(Shapes, checked((int)row.Int(6))) + 1;
            if (type == 0) throw new InvalidDataException("Unsupported native body shape.");
            return type;
        }
        public static CfgTable.Row Resolve(CfgTable models, int type, uint variant, bool female, CfgTable.Row original = null)
        {
            if (variant != 0)
            {
                var exact = models.Rows.FirstOrDefault(r => IsBody(r) && r.UInt(0) == variant);
                if (exact == null || !Matches(exact, type)) throw new InvalidDataException("Body variant does not match the selected shape.");
                return exact;
            }
            var rows = models.Rows.Where(r => Matches(r, type)).ToArray();
            if (Matches(original, type) && (type <= 2 || (original.Int(5) >= 4) == female)) return original;
            return rows.FirstOrDefault(r => (r.Int(5) >= 4) == female) ?? rows.FirstOrDefault();
        }
    }
}
