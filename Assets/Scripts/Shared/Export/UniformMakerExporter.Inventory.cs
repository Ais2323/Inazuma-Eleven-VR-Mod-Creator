using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using G4Composition;
using G4Runtime;

public static partial class UniformMakerExporter
{
    internal static void WriteFantasyShop(IEnumerable<UniformMakerDocument> documents, string data, string modRoot, string modName)
    {
        var uniforms = documents.ToArray();
        if (uniforms.Length == 0) return;

        string nativeDirectory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "item");
        string nativePath = CharacterIdentityCatalog.Find(nativeDirectory, "item_config_");
        string exportedPath = Path.Combine(data, "common", "gamedata", "item", Path.GetFileName(nativePath));
        var items = CfgTable.Read(File.ReadAllBytes(File.Exists(exportedPath) ? exportedPath : nativePath), nativePath);
        var fashion = items.Rows.Where(row => row.Name == "ITEM_FASHION_INFO").ToArray();
        var entries = new StringBuilder("schema = \"VRFW.shop/2\"\n\n[[edit]]\ntarget = \"InazumaFantasy.general_store\"\nif_mod = \"InazumaFantasy\"\n");
        foreach (var uniform in uniforms) {
            CfgTable.Row item;
            if (uniform.isNewUniform) {
                string itemKey = "uni_um_" + Digest(CharacterMakerExportBatch.NamespacedKey(modName, "uniform:" + uniform.newUniformKey));
                item = fashion.SingleOrDefault(row => row.Text(11) == itemKey);
            }
            else item = fashion.FirstOrDefault(row => row.UInt(18) == uniform.targetUniformId);
            if (item == null) throw new InvalidDataException("Missing exported uniform item for Fantasy shop.");

            // Fantasy's curated store does not inherit the retail BB list.
            // Its standard third-party kits cost 21 passion tokens (IECross).
            entries.Append("\n  [[edit.entry]]\n  item = \"0x").Append(item.UInt(0).ToString("X8", System.Globalization.CultureInfo.InvariantCulture))
                .Append("\"\n  price = ").Append(uniform.freeInShop ? 0 : 21)
                .Append("\n  currency = \"tk_bb000001\"\n  stock = 1\n");
        }
        string directory = Path.Combine(modRoot, "content", "shops");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "uniforms.toml"), entries.ToString(), new UTF8Encoding(false));
    }

    static Dictionary<UniformMakerDocument, uint> PrepareInventory(UniformMakerDocument[] docs, string output, string modName, ref byte[] configBytes)
    {
        string itemPath = CharacterIdentityCatalog.Find(Path.Combine(DumpDataManager.CommonPath, "gamedata", "item"), "item_config_");
        string shopPath = CharacterIdentityCatalog.Find(Path.Combine(DumpDataManager.CommonPath, "gamedata", "shop"), "shop_config_");
        string Output(string source) => Path.Combine(output, "common", source.Substring(DumpDataManager.CommonPath.TrimEnd(Path.DirectorySeparatorChar).Length).TrimStart(Path.DirectorySeparatorChar));
        CfgTable Read(string path) => CfgTable.Read(File.ReadAllBytes(File.Exists(Output(path)) ? Output(path) : path), path);
        var items = Read(itemPath); var shop = Read(shopPath);
        var fashion = items.Rows.Where(r => r.Name == "ITEM_FASHION_INFO").ToArray();
        var config = new AvatarRecipeParser.Rdbnp(configBytes, "Uniform inventory");
        var infos = config.GetList("m_UniformInfoList"); var models = config.GetList("m_UniformModelInfoList");
        var ids = new HashSet<uint>(items.Rows.Where(r => r.Types.Length > 0 && r.Types[0] == 1).Select(r => r.UInt(0))
            .Concat(Enumerable.Range(0, infos.Count).Select(i => config.U32(infos.Row(i))))
            .Concat(shop.Rows.Where(r => r.Name == "SHOP_INFO_ITEM").Select(r => r.UInt(0))));
        var created = new Dictionary<UniformMakerDocument, uint>();
        var addedInfos = new List<byte[]>(); var addedModels = new List<byte[]>();
        var textTables = new Dictionary<string, CfgTable>();
        uint Allocate(string key)
        {
            uint id = G4TextureParser.Crc32Utf8(CharacterMakerExportBatch.NamespacedKey(modName, key));
            if (id == 0 || !ids.Add(id)) throw new InvalidDataException("Uniform inventory ID collision: " + key);
            return id;
        }
        void Save(string path, CfgTable table)
        {
            byte[] bytes = table.Write(); CfgTable.Read(bytes, path);
            string target = Output(path); Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllBytes(target, bytes);
        }
        foreach (var doc in docs)
        {
            uint templateId = doc.isNewUniform ? doc.templateUniformId : doc.targetUniformId;
            var template = fashion.FirstOrDefault(r => r.UInt(18) == templateId)
                ?? throw new InvalidDataException("Missing uniform inventory template.");
            uint itemId = template.UInt(0);
            uint nameId = template.UInt(2);
            string iconKey = template.Text(11);
            if (doc.isNewUniform)
            {
                string key = "uniform:" + doc.newUniformKey;
                uint uniformId = Allocate(key + ":config"); nameId = Allocate(key + ":name");
                string itemKey = "uni_um_" + Digest(CharacterMakerExportBatch.NamespacedKey(modName, key));
                itemId = G4TextureParser.Crc32(itemKey);
                if (itemId == 0 || !ids.Add(itemId)) throw new InvalidDataException("Uniform item key collision.");
                created.Add(doc, uniformId);
                int original = Enumerable.Range(0, infos.Count).Select(infos.Row).Single(r => config.U32(r) == templateId);
                uint span = config.U32(original + infos.Field("modelInfo"));
                int first = (int)(span & 0xffff), count = (int)(span >> 16), next = models.Count + addedModels.Count;
                if (next + count > ushort.MaxValue) throw new InvalidDataException("Uniform model reference limit exceeded.");
                var info = configBytes.Skip(original).Take(infos.Stride).ToArray();
                Put(info, 0, uniformId); Put(info, infos.Field("modelInfo"), (uint)(next | count << 16)); addedInfos.Add(info);
                for (int i = 0; i < count; i++) addedModels.Add(configBytes.Skip(models.Row(first + i)).Take(models.Stride).ToArray());
                var item = template.Clone();
                Set(item, 0, itemId); Set(item, 2, nameId); Set(item, 18, uniformId);
                item.Values[11] = itemKey;
                iconKey = itemKey;
                long number = 30000L + G4TextureParser.Crc32Utf8(CharacterMakerExportBatch.NamespacedKey(modName, key + ":number")) % 30000u;
                if (items.Rows.Any(r => r.Name == "ITEM_FASHION_INFO" && r.Int(4) == number)) throw new InvalidDataException("Uniform item number collision.");
                Set(item, 4, number);
                InsertFashionItem(items, item, FashionSeries(items, template.UInt(0)));
            }
            if (doc.isNewUniform || !string.IsNullOrEmpty(doc.iconPngBase64))
                UniformInventoryIcon.Export(output, template.Text(11), iconKey, doc.iconPngBase64);
            if (doc.isNewUniform || doc.names?.Count > 0)
            {
                foreach (string locale in CharacterIdentityCatalog.Locales)
                {
                    string path = Path.Combine(DumpDataManager.CommonPath, "text", locale, "item_text.cfg.bin");
                    if (!File.Exists(path)) continue;
                    if (!textTables.TryGetValue(path, out var text)) textTables.Add(path, text = Read(path));
                    if (doc.isNewUniform && text.Rows.Any(r => r.Name == "NOUN_INFO" && r.UInt(0) == nameId)) throw new InvalidDataException("Uniform name ID collision: " + locale);
                    var noun = text.Rows.FirstOrDefault(r => r.Name == "NOUN_INFO" && r.UInt(0) == template.UInt(2) && r.Int(1) == 0)?.Clone()
                        ?? throw new InvalidDataException("Missing uniform name template: " + locale);
                    Set(noun, 0, nameId); noun.Values[5] = doc.NameFor(locale) ?? noun.Text(5);
                    if (doc.isNewUniform) text.Insert("NOUN_INFO_BEGIN", "NOUN_INFO_END", noun);
                    else foreach (var existing in text.Rows.Where(r => r.Name == "NOUN_INFO" && r.UInt(0) == nameId && r.Int(1) == 0)) existing.Values[5] = noun.Values[5];
                }
            }
            if (doc.isNewUniform || doc.freeInShop) AddUniformSale(shop, fashion, itemId, template.UInt(0),
                Allocate("uniform:shop:" + (doc.isNewUniform ? doc.newUniformKey : doc.targetUniformId.ToString())), doc.freeInShop);
        }
        configBytes = config.AppendRows("m_UniformModelInfoList", addedModels);
        configBytes = new AvatarRecipeParser.Rdbnp(configBytes, "Uniform model append").AppendRows("m_UniformInfoList", addedInfos);
        if (created.Count > 0)
        {
            Save(itemPath, items);
            // Both item registries can be present in the game/merged Mod. Shops
            // resolving the older registry still need the new item's definition.
            var additions = items.Rows.Where(r => r.Name == "ITEM_FASHION_INFO" && created.Values.Contains(r.UInt(18))).ToArray();
            SynchronizeItemRegistries(output, additions);
        }
        foreach (var pair in textTables) Save(pair.Key, pair.Value);
        if (docs.Any(d => d.isNewUniform || d.freeInShop)) Save(shopPath, shop);
        return created;
    }

    internal static void SynchronizeItemRegistries(string output, CfgTable.Row[] additions)
    {
        string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "item");
        string newest = Path.Combine(output, "common", "gamedata", "item", Path.GetFileName(CharacterIdentityCatalog.Find(directory, "item_config_")));
        var definitions = CfgTable.Read(File.ReadAllBytes(newest), newest);
        foreach (string source in Directory.GetFiles(directory, "item_config_*.cfg.bin")
            .Where(p => char.IsDigit(Path.GetFileName(p)["item_config_".Length])))
        {
            string target = Path.Combine(output, "common", "gamedata", "item", Path.GetFileName(source));
            var table = CfgTable.Read(File.ReadAllBytes(File.Exists(target) ? target : source), source);
            bool changed = false;
            foreach (var addition in additions)
            {
                var existing = table.Rows.FirstOrDefault(r => r.Name == "ITEM_FASHION_INFO" && r.UInt(0) == addition.UInt(0));
                if (existing != null)
                {
                    if (existing.UInt(18) != addition.UInt(18) || existing.Text(11) != addition.Text(11))
                        throw new InvalidDataException("Uniform item ID collision in " + Path.GetFileName(source));
                }
                if (table.Rows.Any(r => r.Name == "ITEM_FASHION_INFO" && r.UInt(0) != addition.UInt(0) && r.Int(4) == addition.Int(4)))
                    throw new InvalidDataException("Uniform item number collision in " + Path.GetFileName(source));
                InsertFashionItem(table, addition, FashionSeries(definitions, addition.UInt(0))); changed = true;
            }
            if (!changed) continue;
            byte[] bytes = table.Write(); CfgTable.Read(bytes, target);
            Directory.CreateDirectory(Path.GetDirectoryName(target)); File.WriteAllBytes(target, bytes);
        }
    }

    internal static CfgTable.Row FashionSeries(CfgTable table, uint itemId)
    {
        int index = table.Rows.FindIndex(r => r.Name == "ITEM_FASHION_INFO" && r.UInt(0) == itemId);
        if (index < 0 || index + 1 >= table.Rows.Count || table.Rows[index + 1].Name != "ITEM_FASHION_INFO_SERIES")
            throw new InvalidDataException("Uniform item has no series record: " + itemId);
        return table.Rows[index + 1];
    }

    internal static void InsertFashionItem(CfgTable table, CfgTable.Row item, CfgTable.Row series)
    {
        int begin = table.Rows.FindIndex(r => r.Name == "ITEM_FASHION_INFO_LIST_BEG");
        int end = table.Rows.FindIndex(begin + 1, r => r.Name == "ITEM_FASHION_INFO_LIST_END");
        if (begin < 0 || end < 0 || table.Rows[begin].Values.Length < 2 || series.Name != "ITEM_FASHION_INFO_SERIES")
            throw new InvalidDataException("Invalid uniform item list.");
        var sortTemplate = table.Rows.Skip(begin + 1).Take(end - begin - 1).FirstOrDefault(r => r.Name == "__SORT_INDEX")?.Clone()
            ?? throw new InvalidDataException("Uniform item lookup index template is missing.");
        // Index values refer to logical INFO/SERIES pairs, not physical rows.
        // The old exporter appended INFO after this index, leaving it unfindable.
        for (int i = end - 1; i > begin; i--) if (table.Rows[i].Name == "__SORT_INDEX") table.Rows.RemoveAt(i);
        end = table.Rows.FindIndex(begin + 1, r => r.Name == "ITEM_FASHION_INFO_LIST_END");
        int existing = table.Rows.FindIndex(begin + 1, end - begin - 1, r => r.Name == "ITEM_FASHION_INFO" && r.UInt(0) == item.UInt(0));
        if (existing < 0)
        {
            table.Rows.Insert(end, item.Clone()); table.Rows.Insert(end + 1, series.Clone());
        }
        else if (table.Rows[existing + 1].Name != "ITEM_FASHION_INFO_SERIES") table.Rows.Insert(existing + 1, series.Clone());
        end = table.Rows.FindIndex(begin + 1, r => r.Name == "ITEM_FASHION_INFO_LIST_END");
        var rows = table.Rows.Skip(begin + 1).Take(end - begin - 1).ToArray();
        if (rows.Length % 2 != 0 || rows.Where((r, i) => r.Name != (i % 2 == 0 ? "ITEM_FASHION_INFO" : "ITEM_FASHION_INFO_SERIES")).Any())
            throw new InvalidDataException("Uniform item/series pairs are malformed.");
        var entries = rows.Where(r => r.Name == "ITEM_FASHION_INFO").ToArray();
        if (entries.Select(r => r.UInt(0)).Distinct().Count() != entries.Length) throw new InvalidDataException("Duplicate uniform item IDs.");
        table.Rows[begin].Values[0] = (long)entries.Length;
        table.Rows[begin].Values[1] = 1L;
        var indices = entries.Select((r, i) => new { id = r.UInt(0), index = i }).OrderBy(x => x.id).Select(x =>
        { var row = sortTemplate.Clone(); row.Types[0] = 1; row.Values[0] = (long)x.index; return row; }).ToArray();
        table.Rows.InsertRange(end, indices);
    }

    static void AddUniformSale(CfgTable shop, CfgTable.Row[] fashion, uint itemId, uint templateItemId, uint shopItemId, bool free)
    {
        // Verified native BB shop name ID (SHOP_INFO[1], localized as BB Shop).
        int start = shop.Rows.FindIndex(r => r.Name == "SHOP_INFO" && r.UInt(1) == 37697836u);
        if (start < 0) throw new InvalidDataException("BB shop not found.");
        int begin = shop.Rows.FindIndex(start + 1, r => r.Name == "SHOP_INFO_ITEM_LIST_BEG");
        int end = shop.Rows.FindIndex(begin + 1, r => r.Name == "SHOP_INFO_ITEM_LIST_END");
        if (begin < 0 || end < 0) throw new InvalidDataException("BB shop item list missing.");
        var rows = shop.Rows.Skip(begin + 1).Take(end - begin - 1).Where(r => r.Name == "SHOP_INFO_ITEM").ToArray();
        var item = rows.FirstOrDefault(r => r.UInt(2) == itemId);
        if (item == null)
        {
            var nativeIds = new HashSet<uint>(fashion.Select(r => r.UInt(0)));
            item = (rows.FirstOrDefault(r => r.UInt(2) == templateItemId) ?? rows.FirstOrDefault(r => nativeIds.Contains(r.UInt(2))))?.Clone()
                ?? throw new InvalidDataException("BB uniform sale template missing.");
            Set(item, 0, shopItemId); Set(item, 2, itemId);
            long order = shop.Rows.Where(r => r.Name == "SHOP_INFO_ITEM").Max(r => r.Int(23)) + 1;
            Set(item, 6, order); Set(item, 23, order);
            shop.Rows.Insert(end, item); Set(shop.Rows[begin], 0, shop.Rows[begin].Int(0) + 1);
        }
        if (free) for (int field = 7; field <= 21; field++) Set(item, field, 0);
        // Unconditional native BB goods use an integer zero, not a null string
        // reference (-1). Match the variant type as well as the scalar value.
        item.Types[22] = 1; Set(item, 22, 0);
    }
    static void Set(CfgTable.Row row, int field, long value) => row.Values[field] = unchecked((long)(int)value);
}
