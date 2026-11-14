using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using G4Composition;

public static partial class UniformMakerExporter
{
    static string FantasyScalar(object value)
    {
        if (value is string text) return Newtonsoft.Json.JsonConvert.ToString(text);
        return value == null ? "0" : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    static string FantasyId(uint value) => "0x" + value.ToString("X8", CultureInfo.InvariantCulture);

    static void FantasyWrite(string root, string relative, StringBuilder text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path, text.ToString(), new UTF8Encoding(false));
    }

    static bool FantasySameRow(CfgTable.Row a, CfgTable.Row b) => a != null && b != null &&
        a.Name == b.Name && a.Types.SequenceEqual(b.Types) && a.Values.SequenceEqual(b.Values);

    // Convert only the uniform edits into declarations. The framework appends
    // referenced rows and rebuilds lookup indexes after merging all mods.
    internal static void WriteFantasyUniformResources(string data, string modRoot, string modName)
    {
        string directory = Path.Combine(data, "common", "gamedata", "character");
        if (!Directory.Exists(directory)) return;
        string configPath = Directory.GetFiles(directory, "uniform_config_*.cfg.bin").SingleOrDefault();
        if (configPath == null) return;
        string partsPath = Directory.GetFiles(directory, "chara_parts_*.cfg.bin").Single();
        var parts = CfgTable.Read(File.ReadAllBytes(partsPath), partsPath);
        var nativeParts = CfgTable.Read(File.ReadAllBytes(Path.Combine(DumpDataManager.CommonPath, "gamedata", "character", Path.GetFileName(partsPath))), partsPath);
        var looks = new StringBuilder("schema = \"VRFW.look/2\"\n");
        foreach (var kind in new[] { ("clothes", "CHARA_PARTS_CLOTHES"), ("gloves", "CHARA_PARTS_GLOVE") }) {
            var nativeModels = nativeParts.Rows.Where(row => row.Name == kind.Item2 + "_MODEL").ToDictionary(row => row.UInt(0));
            foreach (var model in parts.Rows.Where(row => row.Name == kind.Item2 + "_MODEL")) {
                var rows = Referenced(parts, model, kind.Item2 + "_INFO");
                if (nativeModels.TryGetValue(model.UInt(0), out var original)) {
                    var nativeRows = Referenced(nativeParts, original, kind.Item2 + "_INFO");
                    if (rows.Length == nativeRows.Length && rows.Zip(nativeRows, FantasySameRow).All(equal => equal)) continue;
                }
                looks.Append("\n[[").Append(kind.Item1).Append("]]\nid = ").Append(FantasyId(model.UInt(0))).Append('\n');
                if (kind.Item1 == "clothes") looks.Append("model = ").Append(FantasyScalar(model.Text(1))).Append('\n');
                looks.Append("rows = [\n");
                foreach (var row in rows) looks.Append("  [").Append(string.Join(", ", row.Values.Select(FantasyScalar))).Append("],\n");
                looks.Append("]\n");
            }
        }

        var colors = new StringBuilder("schema = \"VRFW.base_table/1\"\n\n[[keyed]]\ntable = \"character/chara_parts_color\"\n");
        var nativeColors = nativeParts.Rows.Where(row => row.Name == "CHARA_PARTS_COLOR").ToDictionary(row => row.UInt(0));
        bool changedColors = false;
        foreach (var row in parts.Rows.Where(row => row.Name == "CHARA_PARTS_COLOR")) {
            bool existing = nativeColors.TryGetValue(row.UInt(0), out var original);
            if (FantasySameRow(row, original)) continue;
            changedColors = true;
            colors.Append(existing ? "\n[[keyed.set]]\n" : "\n[[keyed.add]]\n")
                .Append("key = ").Append(FantasyId(row.UInt(0))).Append("\ncells = { color_1 = ")
                .Append(FantasyId(row.UInt(1))).Append(", color_2 = ").Append(FantasyId(row.UInt(2)))
                .Append(", reserved_3 = ").Append(FantasyScalar(row.Values[3])).Append(" }\n");
        }
        if (changedColors) FantasyWrite(modRoot, "content/base_tables/gamedata/character/chara_parts.toml", colors);

        FantasyKits(configPath, looks);
        FantasyWrite(modRoot, "content/looks/uniforms.toml", looks);
        File.Delete(configPath);
        File.Delete(partsPath);
        FantasyItems(data, modRoot, modName);
    }

    static void FantasyKits(string path, StringBuilder looks)
    {
        byte[] outputBytes = File.ReadAllBytes(path);
        byte[] nativeBytes = File.ReadAllBytes(Path.Combine(DumpDataManager.CommonPath, "gamedata", "character", Path.GetFileName(path)));
        var output = new AvatarRecipeParser.Rdbnp(outputBytes, path);
        var native = new AvatarRecipeParser.Rdbnp(nativeBytes, path);
        var infos = output.GetList("m_UniformInfoList");
        var models = output.GetList("m_UniformModelInfoList");
        var nativeInfos = native.GetList("m_UniformInfoList");
        var nativeModels = native.GetList("m_UniformModelInfoList");
        var nativeById = Enumerable.Range(0, nativeInfos.Count).ToDictionary(i => native.U32(nativeInfos.Row(i)), nativeInfos.Row);
        byte[] ModelRows(AvatarRecipeParser.Rdbnp.List list, uint span, byte[] bytes)
        {
            int first = (int)(span & 0xffff), count = (int)(span >> 16);
            return count == 0 ? Array.Empty<byte>() : bytes.Skip(list.Row(first)).Take(count * list.Stride).ToArray();
        }
        var primary = new Dictionary<string, string> {
            ["uniformFielderModelIdCrc"] = "fielder", ["uniformKeeperModelIdCrc"] = "keeper",
            ["shoesFielderModelIdCrc"] = "shoes", ["gloveModelIdCrc"] = "glove"
        };
        for (int i = 0; i < infos.Count; i++) {
            int info = infos.Row(i);
            uint id = output.U32(info), span = output.U32(info + infos.Field("modelInfo"));
            bool existing = nativeById.TryGetValue(id, out int original);
            if (existing && ModelRows(models, span, outputBytes).SequenceEqual(
                ModelRows(nativeModels, native.U32(original + nativeInfos.Field("modelInfo")), nativeBytes))) continue;
            // Kit info contains its id and model-list reference. Each model's
            // complete cells below preserve the native style and equipment flags.
            uint template = existing ? id : native.U32(nativeInfos.Row(0));
            looks.Append("\n[[kit]]\nid = ").Append(FantasyId(id)).Append("\nfrom = ").Append(FantasyId(template)).Append('\n');
            for (int j = 0; j < (span >> 16); j++) {
                int row = models.Row((int)(span & 0xffff) + j);
                looks.Append("\n  [[kit.model]]\n  type = ").Append(output.Byte(row + models.Field("typeId"))).Append('\n');
                foreach (var field in primary)
                    looks.Append("  ").Append(field.Value).Append(" = ").Append(FantasyId(output.UnsignedField(models, row, field.Key))).Append('\n');
                var cells = models.FieldNames.Where(name => name != "typeId" && !primary.ContainsKey(name))
                    .Select(name => name + " = " + FantasyId(output.UnsignedField(models, row, name)));
                looks.Append("  cells = { ").Append(string.Join(", ", cells)).Append(" }\n");
            }
        }
    }

    static void FantasyItems(string data, string modRoot, string modName)
    {
        string nativeDirectory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "item");
        string nativePath = CharacterIdentityCatalog.Find(nativeDirectory, "item_config_");
        string directory = Path.Combine(data, "common", "gamedata", "item");
        string outputPath = Path.Combine(directory, Path.GetFileName(nativePath));
        var native = CfgTable.Read(File.ReadAllBytes(nativePath), nativePath);
        var output = File.Exists(outputPath) ? CfgTable.Read(File.ReadAllBytes(outputPath), outputPath) : native;
        var originalItems = native.Rows.Where(row => row.Name == "ITEM_FASHION_INFO").ToDictionary(row => row.UInt(0));
        var changed = output.Rows.Where(row => row.Name == "ITEM_FASHION_INFO" &&
            (!originalItems.TryGetValue(row.UInt(0), out var original) || !FantasySameRow(row, original))).ToArray();
        var fields = new[] { "id", "reserved_1", "name_text", "desc_text", "catalog_no", "category", "sub_category", "max_stack",
            "reserved_8", "reserved_9", "reserved_10", "key", "rarity", "reserved_13", "base_item", "icon_index", "reserved_16", "const_17", "uniform" };
        var definitions = new StringBuilder("schema = \"VRFW.base_table/1\"\n\n[[keyed]]\ntable = \"item/item_fashion\"\n");
        foreach (var item in changed) {
            bool existing = originalItems.ContainsKey(item.UInt(0));
            definitions.Append(existing ? "\n[[keyed.set]]\n" : "\n[[keyed.add]]\n").Append("key = ").Append(FantasyId(item.UInt(0))).Append('\n');
            if (!existing) {
                var series = FashionSeries(output, item.UInt(0));
                var template = originalItems.Values.First(row => FashionSeries(native, row.UInt(0)).Values.SequenceEqual(series.Values));
                definitions.Append("from = ").Append(FantasyId(template.UInt(0))).Append('\n');
            }
            if (item.Values.Length != fields.Length) throw new InvalidDataException("Unsupported Fantasy uniform item columns.");
            definitions.Append("cells = { ").Append(string.Join(", ", Enumerable.Range(1, fields.Length - 1)
                .Select(index => fields[index] + " = " + FantasyScalar(item.Values[index])))).Append(" }\n");
        }
        if (changed.Length > 0) FantasyWrite(modRoot, "content/base_tables/gamedata/item/item_config.toml", definitions);
        FantasyItemTexts(data, modRoot, changed.SelectMany(row => new[] { row.UInt(2), row.UInt(3) }).Where(id => id != 0).Distinct());
        FantasyItemIcons(data, modRoot, modName);
        if (Directory.Exists(directory)) foreach (string file in Directory.GetFiles(directory, "item_config_*.cfg.bin")) File.Delete(file);
        string shopDirectory = Path.Combine(data, "common", "gamedata", "shop");
        if (Directory.Exists(shopDirectory)) foreach (string file in Directory.GetFiles(shopDirectory, "shop_config_*.cfg.bin")) File.Delete(file);
    }

    static void FantasyItemTexts(string data, string modRoot, IEnumerable<uint> ids)
    {
        var text = new StringBuilder("schema = \"VRFW.text/2\"\n");
        var tables = new Dictionary<string, CfgTable>();
        var changedIds = new HashSet<uint>(ids);
        foreach (string locale in CharacterIdentityCatalog.Locales) {
            string relative = Path.Combine("text", locale, "item_text.cfg.bin");
            string path = Path.Combine(data, "common", relative);
            if (!File.Exists(path)) continue;
            var table = CfgTable.Read(File.ReadAllBytes(path), path);
            tables.Add(locale, table);
            string nativePath = Path.Combine(DumpDataManager.CommonPath, relative);
            var native = CfgTable.Read(File.ReadAllBytes(nativePath), nativePath).Rows
                .Where(row => row.Name == "NOUN_INFO" && row.Int(1) == 0).ToDictionary(row => row.UInt(0));
            foreach (var row in table.Rows.Where(row => row.Name == "NOUN_INFO" && row.Int(1) == 0))
                if (!native.TryGetValue(row.UInt(0), out var original) || row.Text(5) != original.Text(5)) changedIds.Add(row.UInt(0));
        }
        foreach (uint id in changedIds.OrderBy(value => value)) {
            var rows = tables.Select(pair => new { Locale = pair.Key, Row = pair.Value.Rows.FirstOrDefault(row => row.Name == "NOUN_INFO" && row.UInt(0) == id && row.Int(1) == 0) })
                .Where(pair => pair.Row != null).ToArray();
            if (rows.Length == 0) continue;
            text.Append("\n[[row]]\nid = ").Append(FantasyId(id)).Append("\nkind = \"noun\"\n");
            foreach (var row in rows) text.Append(row.Locale).Append(" = ").Append(FantasyScalar(row.Row.Text(5))).Append('\n');
        }
        if (tables.Count == 0) return;
        FantasyWrite(modRoot, "text/rows/item_text.toml", text);
        foreach (string locale in tables.Keys) File.Delete(Path.Combine(data, "common", "text", locale, "item_text.cfg.bin"));
    }

    static void FantasyItemIcons(string data, string modRoot, string modName)
    {
        string path = Path.Combine(data, "dx11", UniformInventoryIcon.RelativePath);
        if (!File.Exists(path)) return;
        byte[] bytes = File.ReadAllBytes(path), nativeBytes = File.ReadAllBytes(Path.Combine(DumpDataManager.Dx11Path, UniformInventoryIcon.RelativePath));
        var native = RawTexture.Read(nativeBytes, path).ToDictionary(texture => texture.Name);
        var textures = RawTexture.Read(bytes, path).Where(texture => !native.TryGetValue(texture.Name, out var original) || !SameTexturePayload(texture, original)).ToArray();
        if (textures.Length > 0) {
            string atlas = "menu/200_icon/02_icon_item/cm_" + Digest(CharacterMakerExportBatch.NamespacedKey(modName, "uniform-icons")) + ".g4tx";
            string target = Path.Combine(data, "dx11", atlas);
            File.WriteAllBytes(target, RawTexture.WriteNativeLayout(nativeBytes, bytes, textures));
            var redirect = new StringBuilder("schema = \"VRFW.sprite_redirect/1\"\n\n[[VRFW.SpriteRedirect]]\nname = \"uniform_icons\"\natlas = \"#/menu/200_icon/02_icon_item/icon_item07.g4tx\"\nown_atlas = ")
                .Append(FantasyScalar("#/" + atlas)).Append("\nsprites = [").Append(string.Join(", ", textures.Select(texture => FantasyScalar(texture.Name)))).Append("]\n");
            FantasyWrite(modRoot, "content/sprite_redirects/uniform_icons.toml", redirect);
        }
        File.Delete(path);
    }
}
