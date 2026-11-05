using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;

// Match uniforms come from inventory entries, not every clothes model (which
// also includes casual clothes, cutscene costumes and alternate body meshes).
internal sealed class UniformCatalog
{
    internal sealed class Entry
    {
        internal uint Id, TextId;
        internal string Key;
        internal readonly uint[,] Models = new uint[2, 2]; // home/away, fielder/keeper
        internal readonly uint[] Gloves = new uint[2];
    }

    internal sealed class GloveSelection
    {
        internal string Model, Texture;
    }
    internal sealed class ModelOption
    {
        internal uint Id;
        internal string Name, TypeKey;
    }
    internal readonly List<ModelOption> ClothesOptions = new(), ArmbandOptions = new(), GloveOptions = new();

    internal readonly List<Entry> Entries = new();
    internal readonly CfgTable.Row Body;
    readonly CfgTable bodyModels;
    readonly CfgTable parts;
    readonly string root;
    readonly Dictionary<string, Dictionary<uint, string>> names = new();
    readonly Dictionary<uint, CfgTable.Row[]> clothesRows = new(), gloveRows = new();

    // A menu entry is a native model family, not a texture, team or body shape.
    // Each family's clothes record retains its own u/sk/n references for all bodies.
    internal List<ModelOption> OptionsFor(UniformPartKind kind) => kind == UniformPartKind.Upper
        ? ClothesOptions : kind == UniformPartKind.Armband ? ArmbandOptions : GloveOptions;

    internal bool MatchesModel(UniformPartKind kind, ModelOption option, uint id)
        => id != 0 && option.TypeKey == ModelTypeKey(kind, id);

    string ModelTypeKey(UniformPartKind kind, uint id)
    {
        var lookup = kind == UniformPartKind.Gloves ? gloveRows : clothesRows;
        if (!lookup.TryGetValue(id, out var rows)) return null;
        int field = kind == UniformPartKind.Armband ? 8 : 0;
        string path = rows.Select(r => r.Text(field)).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));
        if (path == null) return null;
        // Native families keep their body variants together (u000101 contains
        // u000101..u000108, for example). Never group by the selected body.
        return Path.GetDirectoryName(path)?.Replace('\\', '/').ToLowerInvariant();
    }

    internal UniformCatalog()
    {
        root = DumpDataManager.CommonPath;
        string character = Path.Combine(root, "gamedata", "character");
        bodyModels = CfgTable.Read(File.ReadAllBytes(CharacterIdentityCatalog.Find(character, "chara_model_")), "Uniform preview body");
        Body = NativeBodyTypes.ResolveForGender(bodyModels, 1, 0, 1)
            ?? throw new InvalidDataException("Missing default uniform preview body.");
        string partsPath = CharacterIdentityCatalog.Find(character, "chara_parts_");
        parts = CfgTable.Read(File.ReadAllBytes(partsPath), partsPath);
        string configPath = CharacterIdentityCatalog.Find(character, "uniform_config_");
        var config = new AvatarRecipeParser.Rdbnp(File.ReadAllBytes(configPath), configPath);
        var uniforms = config.GetList("m_UniformInfoList");
        var models = config.GetList("m_UniformModelInfoList");
        var byId = new Dictionary<uint, int>();
        for (int i = 0; i < uniforms.Count; i++) byId[config.U32(uniforms.Row(i))] = uniforms.Row(i);
        string itemPath = CharacterIdentityCatalog.Find(Path.Combine(root, "gamedata", "item"), "item_config_");
        var items = CfgTable.Read(File.ReadAllBytes(itemPath), itemPath);
        var seen = new HashSet<uint>();
        foreach (var item in items.Rows.Where(r => r.Name == "ITEM_FASHION_INFO"))
        {
            uint id = item.UInt(18);
            if (!byId.TryGetValue(id, out int row) || !seen.Add(id)) continue;
            var entry = new Entry { Id = id, TextId = item.UInt(2), Key = item.Text(11) };
            uint reference = config.U32(row + 4);
            int first = (int)(reference & 0xffff), count = (int)(reference >> 16);
            for (int j = 0; j < count; j++)
            {
                int model = models.Row(first + j), type = config.Byte(model + 92);
                if (type > 1) continue;
                for (int role = 0; role < 2; role++) entry.Models[type, role] = config.U32(model + role * 4);
                entry.Gloves[type] = config.U32(model + models.Field("gloveModelIdCrc"));
            }
            if (entry.Models[0, 0] != 0) Entries.Add(entry);
        }
        BuildModelOptions();
    }

    internal string Name(Entry entry, string locale)
    {
        if (!names.TryGetValue(locale, out var lookup))
        {
            string path = Path.Combine(root, "text", locale, "item_text.cfg.bin");
            if (!File.Exists(path)) path = Path.Combine(root, "text", "en", "item_text.cfg.bin");
            lookup = CfgTable.Read(File.ReadAllBytes(path), path).Rows
                .Where(r => r.Name == "NOUN_INFO" && r.Int(1) == 0)
                .GroupBy(r => r.UInt(0)).ToDictionary(g => g.Key, g => g.First().Text(5));
            names.Add(locale, lookup);
        }
        return lookup.TryGetValue(entry.TextId, out string name) && !string.IsNullOrWhiteSpace(name) ? name : entry.Key;
    }

    internal CfgTable.Row BodyForType(int type) => type >= 1 && type <= 8
        ? NativeBodyTypes.ResolveForGender(bodyModels, type, 0, type == 2 ? 2 : 1) : null;
    internal CfgTable.Row BodyByAttribute(int attribute) => bodyModels.Rows.FirstOrDefault(r =>
        r.Name == "CHARA_BODY_INFO" && r.Values.Length >= 7 && r.Int(4) == attribute) ?? Body;

    internal NativeUniformFashion.Selection Resolve(Entry entry, int side, int role,
        CfgTable.Row body = null, int sleeve = 0, int hem = 0, uint overrideId = 0) =>
        (overrideId == 0 ? entry.Models[side, role] : overrideId) == 0 ? null :
        NativeUniformFashion.Resolve(overrideId == 0 ? entry.Models[side, role] : overrideId, body ?? Body, sleeve, hem);

    internal GloveSelection ResolveGloves(Entry entry, int side, CfgTable.Row body)
        => ResolveGloves(entry.Gloves[side], body);

    internal GloveSelection ResolveGloves(uint id, CfgTable.Row body)
    {
        if (id == 0) return null;
        int index = parts.Rows.FindIndex(r => r.Name == "CHARA_PARTS_GLOVE_MODEL" && r.UInt(0) == id);
        if (index < 0 || index + 1 >= parts.Rows.Count || parts.Rows[index + 1].Name != "CHARA_PARTS_GLOVE_MODEL_REF_INFO")
            throw new InvalidDataException("Missing uniform glove reference: " + id.ToString("X8"));
        var reference = parts.Rows[index + 1];
        var rows = parts.Rows.Where(r => r.Name == "CHARA_PARTS_GLOVE_INFO")
            .Skip((int)reference.Int(0)).Take((int)reference.Int(1)).ToArray();
        if (rows.Length == 0) throw new InvalidDataException("Empty uniform glove reference: " + id.ToString("X8"));
        // Gloves have four skeleton families, shared between male/female rows;
        // this is CHARA_BODY_INFO[5], not the eight visible body shape indices.
        int family = (int)body.Int(5) % 4;
        var selected = rows.FirstOrDefault(r => r.Int(2) == family) ?? rows[0];
        string texture = selected.Text(1);
        if (string.IsNullOrWhiteSpace(texture)) texture = rows[0].Text(1);
        if (string.IsNullOrWhiteSpace(texture)) throw new InvalidDataException("Missing uniform glove texture: " + id.ToString("X8"));
        return new GloveSelection { Model = selected.Text(0), Texture = texture };
    }

    void BuildModelOptions()
    {
        var clothes = parts.Rows.Where(r => r.Name == "CHARA_PARTS_CLOTHES_INFO").ToArray();
        var gloves = parts.Rows.Where(r => r.Name == "CHARA_PARTS_GLOVE_INFO").ToArray();
        for (int i = 0; i + 1 < parts.Rows.Count; i++)
        {
            var row = parts.Rows[i];
            bool isClothes = row.Name == "CHARA_PARTS_CLOTHES_MODEL", isGloves = row.Name == "CHARA_PARTS_GLOVE_MODEL";
            if (!isClothes && !isGloves) continue;
            var reference = parts.Rows[i + 1];
            if (reference.Name != row.Name + "_REF_INFO") continue;
            (isClothes ? clothesRows : gloveRows)[row.UInt(0)] = (isClothes ? clothes : gloves)
                .Skip((int)reference.Int(0)).Take((int)reference.Int(1)).ToArray();
        }
        // Only inventory match uniforms. The parts table also contains school,
        // casual, cutscene and character-specific clothes in the same directory.
        var matchClothes = new HashSet<uint>(Entries.SelectMany(e => e.Models.Cast<uint>()).Where(id => id != 0));
        var matchGloves = new HashSet<uint>(Entries.SelectMany(e => e.Gloves).Where(id => id != 0));
        void AddTypes(UniformPartKind kind, IEnumerable<uint> ids, List<ModelOption> target)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (uint id in ids.OrderBy(v => v))
            {
                string key = ModelTypeKey(kind, id);
                if (string.IsNullOrEmpty(key) || !seen.Add(key)) continue;
                target.Add(new ModelOption { Id = id, Name = Path.GetFileName(key), TypeKey = key });
            }
            target.Sort((a, b) => StringComparer.Ordinal.Compare(a.Name, b.Name));
        }
        AddTypes(UniformPartKind.Upper, matchClothes, ClothesOptions);
        AddTypes(UniformPartKind.Armband, matchClothes, ArmbandOptions);
        AddTypes(UniformPartKind.Gloves, matchGloves, GloveOptions);
    }

    internal static string Locale
    {
        get
        {
            string locale = LocalizationManager.NowLoc;
            if (locale == "zh-tw" || locale == "zhtw") return "zh_hant";
            if (locale == "zh-cn" || locale == "zhcn") return "zh_hans";
            if (locale == "jp") return "ja";
            return CharacterIdentityCatalog.Locales.Contains(locale) ? locale : "en";
        }
    }
}
