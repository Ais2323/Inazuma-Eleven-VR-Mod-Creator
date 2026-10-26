using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using G4Runtime;

namespace G4Composition
{
    public sealed class CharacterCatalog
    {
        public string Common;
        public CfgTable Base, Parameters, Models, Shop;
        public Dictionary<uint, uint> Skills;
        public readonly Dictionary<uint, string> SkillNames = new Dictionary<uint, string>();
        public readonly Dictionary<string, CfgTable> TextTables = new Dictionary<string, CfgTable>();
        public IEnumerable<CfgTable.Row> Templates
        {
            get
            {
                var playable = new HashSet<uint>(Parameters.Rows.Where(p => p.Name == "CHARA_PARAM_INFO").Select(p => p.UInt(1)));
                return Base.Rows.Where(r => r.Name == "CHARA_BASE_INFO" && r.Values.Length >= 34 && r.Int(31) == 0 && r.Values.Skip(21).Take(10).Any(v => Convert.ToInt64(v) != 0) && playable.Contains(r.UInt(0)));
            }
        }
        static CfgTable Table(string directory, string prefix)
        {
            var paths = Directory.GetFiles(directory, prefix + "*.cfg.bin").Where(p => System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(p), "^" + prefix + @"\d")).ToArray();
            if (paths.Length != 1) throw new InvalidDataException(prefix + ": 無法唯一確認資料表版本。");
            return CfgTable.Read(File.ReadAllBytes(paths[0]), paths[0]);
        }
        public static CharacterCatalog Read(string common, CancellationToken token)
        {
            string directory = Path.Combine(common, "gamedata", "character");
            var c = new CharacterCatalog { Common = common, Base = Table(directory, "chara_base_"), Parameters = Table(directory, "chara_param_"), Models = Table(directory, "chara_model_"), Shop = Table(Path.Combine(common, "gamedata", "shop"), "shop_config_") };
            string[] skills = Directory.GetFiles(Path.Combine(common, "gamedata", "skill"), "skill_config_*.cfg.bin");
            if (skills.Length != 1) throw new InvalidDataException("Skill table version is ambiguous.");
            c.Skills = SkillInventory.Read(File.ReadAllBytes(skills[0]), skills[0]);
            foreach (string aura in Directory.GetFiles(Path.Combine(common, "gamedata", "skill"), "aura_skill_config*.cfg.bin"))
                foreach (var row in CfgTable.Read(File.ReadAllBytes(aura), aura).Rows.Where(r => r.Name == "AURA_CMD_INFO" && r.Values.Length > 2)) c.Skills[row.UInt(0)] = row.UInt(2);
            string skillText = Path.Combine(common, "text", "zh_hant", "skill_text.cfg.bin");
            if (!File.Exists(skillText)) skillText = Path.Combine(common, "text", "en", "skill_text.cfg.bin");
            if (File.Exists(skillText))
            {
                var text = CfgTable.Read(File.ReadAllBytes(skillText), skillText).Rows.Where(r => r.Name == "NOUN_INFO" && r.Values.Length >= 6 && r.Int(1) == 0).GroupBy(r => r.UInt(0)).ToDictionary(g => g.Key, g => g.First().Text(5));
                foreach (var skill in c.Skills) if (text.TryGetValue(skill.Value, out string label)) c.SkillNames[skill.Key] = label;
            }
            foreach (string locale in Directory.GetDirectories(Path.Combine(common, "text")))
                foreach (string name in new[] { "chara_text.cfg.bin", "chara_description_text.cfg.bin", "chara_text_roma.cfg.bin" })
                {
                    token.ThrowIfCancellationRequested(); string path = Path.Combine(locale, name);
                    if (File.Exists(path)) c.TextTables.Add(path, CfgTable.Read(File.ReadAllBytes(path), path));
                }
            if (!c.TextTables.Keys.Any(p => Path.GetFileName(p) == "chara_text.cfg.bin")) throw new InvalidDataException("No character localization tables.");
            return c;
        }
        public string Face(CfgTable.Row character)
        {
            var model = Models.Rows.Single(r => r.Name == "CHARA_MODEL_INFO" && r.UInt(0) == character.UInt(6));
            string path = model.Text(10)?.Replace('\\', '/');
            if (path == null || !path.StartsWith("_face/", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("範本沒有受支援的頭部路徑。");
            return Path.ChangeExtension(path.Substring(6), ".g4md").Replace('/', Path.DirectorySeparatorChar);
        }
    }
    public sealed class NewCharacterWriter
    {
        public sealed class Result
        {
            public string InternalName, ModelRelativePath, PortraitTemplate, SourcePortraitStem;
            public uint CharacterId, ParameterId, ModelId, ShopId;
            public Dictionary<uint, uint> ParameterMap = new();
            public Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
            public Dictionary<string, string> SourceHashes = new Dictionary<string, string>();
        }
        static CfgTable.Row Single(CfgTable table, string type, uint id) => table.Rows.Single(r => r.Name == type && r.UInt(0) == id);
        static void Set(CfgTable.Row row, int index, long value) { if (row.Types[index] != 1) throw new InvalidDataException(row.Name + ": expected integer field " + index); row.Values[index] = value; }
        public static string StableInternalName(string key)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Missing persistent new character key.");
            return "c" + (90000000 + G4TextureParser.Crc32(key) % 9000000).ToString("D8");
        }
        public Result Build(CharacterCatalog catalog, ExportRequest request, string dx11, CancellationToken token)
        {
            var verifiedVersions = new Dictionary<CfgTable, string>
            {
                { catalog.Base, "chara_base_1.03.98.00.cfg.bin" }, { catalog.Parameters, "chara_param_1.03.66.00.cfg.bin" },
                { catalog.Models, "chara_model_1.03.49.00.cfg.bin" }, { catalog.Shop, "shop_config_3.00.22.cfg.bin" }
            };
            foreach (var version in verifiedVersions)
                if (Path.GetFileName(version.Key.Source) != version.Value) throw new InvalidDataException("新增角色尚未驗證此資料表版本：" + version.Key.Source);
            var settings = request.Character;
            if (string.IsNullOrWhiteSpace(settings.DisplayName) || settings.DisplayName.IndexOf('\0') >= 0) throw new InvalidDataException("請輸入角色名稱。");
            if (settings.StatModifiers?.Length != 7 || settings.Skills?.Length != 9 || settings.UnlockLevels?.Length != 9) throw new InvalidDataException("角色欄位數量錯誤。");
            if (settings.Affinity < 0 || settings.Affinity > 4 || settings.MainPosition < 1 || settings.MainPosition > 4 || settings.SubPosition < 0 || settings.SubPosition > 4) throw new InvalidDataException("屬性／位置超出範圍。");
            for (int i = 0; i < 9; i++) if (settings.Skills[i] != 0 && !catalog.Skills.ContainsKey(settings.Skills[i]) || settings.UnlockLevels[i] < 0 || settings.UnlockLevels[i] > 99) throw new InvalidDataException("招式或解鎖等級無效：" + (i + 1));
            var source = catalog.Templates.SingleOrDefault(r => r.UInt(0) == settings.TemplateBaseId) ?? throw new InvalidDataException("範本不是可用的普通角色。");
            var parameter = Single(catalog.Parameters, "CHARA_PARAM_INFO", settings.TemplateParameterId);
            if (parameter.Values.Length < 43 || parameter.UInt(1) != source.UInt(0)) throw new InvalidDataException("能力變體不屬於範本角色。");
            if (!string.Equals(catalog.Face(source), request.TargetRelativePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("新增角色的目標骨架必須使用所選範本頭部。");
            var model = Single(catalog.Models, "CHARA_MODEL_INFO", source.UInt(6));
            var all = new[] { catalog.Base, catalog.Parameters, catalog.Models, catalog.Shop }.Concat(catalog.TextTables.Values).ToArray();
            var occupied = new HashSet<uint>(all.SelectMany(t => t.Rows).SelectMany(r => r.Values.Select((v, i) => new { v, type = r.Types[i] })).Where(v => v.type == 1).Select(v => unchecked((uint)Convert.ToInt64(v.v))));
            var result = new Result();
            foreach (var table in all) result.SourceHashes.Add(table.Source, RawHeadAsset.Digest(File.ReadAllBytes(table.Source)));
            // Identity must never depend on batch order or which other mods were exported first.
            string name = StableInternalName(request.Document.Id);
            string[] keys = { name, "pc_para_" + name, "model_" + name, "name_" + name, "short_" + name, "upper_" + name, "desc_" + name, "shop_" + name };
            uint[] allocated = keys.Select(G4TextureParser.Crc32).ToArray();
            if (allocated.Distinct().Count() != allocated.Length || allocated.Any(occupied.Contains) ||
                File.Exists(Path.Combine(dx11, "menu", "200_icon", "10_icon_chr", "face", name + "_l.g4tx")))
                throw new InvalidDataException("角色編號衝突：" + name + "。為保留更新後的角色身分，匯出已停止，不會自動改號。請將模組匯出到乾淨的資料夾，並確認沒有其他模組使用此編號。");
            result.InternalName = name; result.CharacterId = allocated[0]; result.ParameterId = allocated[1]; result.ModelId = allocated[2]; result.ShopId = allocated[7];
            result.ModelRelativePath = Path.Combine("99_CUSTOM", name, name + ".g4md");
            var clone = source.Clone(); Set(clone, 0, allocated[0]); clone.Values[1] = name;
            Set(clone, 2, catalog.Base.Rows.Where(r => r.Name == "CHARA_BASE_INFO").Max(r => r.Int(2)) + 1);
            for (int i = 0; i < 3; i++) Set(clone, 3 + i, allocated[3 + i]);
            Set(clone, 6, allocated[2]); Set(clone, 19, allocated[6]);
            if (clone.Int(21) != 0) Set(clone, 21, catalog.Base.Rows.Where(r => r.Name == "CHARA_BASE_INFO").Max(r => r.Int(21)) + 1);
            int sourceIndex = catalog.Base.Rows.IndexOf(source);
            if (catalog.Base.Rows[sourceIndex + 1].Name != "CHARA_BASE_INFO_REF_BATTLE") throw new InvalidDataException("Missing base battle-reference companion.");
            var companion = catalog.Base.Rows[sourceIndex + 1].Clone();
            if (companion.Int(0) != 0)
            {
                var battles = catalog.Base.Rows.Where(r => r.Name == "CHARA_BASE_BATTLE").ToArray(); int battle = checked((int)companion.Int(0));
                if (battle < 0 || battle >= battles.Length) throw new InvalidDataException("Invalid battle reference.");
                catalog.Base.Insert("CHARA_BASE_BATTLE_LIST_BEG", "CHARA_BASE_BATTLE_LIST_END", battles[battle].Clone()); Set(companion, 0, battles.Length);
            }
            catalog.Base.Insert("CHARA_BASE_INFO_LIST_BEG", "CHARA_BASE_INFO_LIST_END", clone, companion);
            var parameterClone = parameter.Clone(); Set(parameterClone, 0, allocated[1]); Set(parameterClone, 1, allocated[0]);
            Set(parameterClone, 2, settings.Affinity); Set(parameterClone, 3, settings.MainPosition); Set(parameterClone, 4, settings.SubPosition);
            for (int i = 0; i < 9; i++) { Set(parameterClone, 11 + i * 2, settings.Skills[i]); Set(parameterClone, 12 + i * 2, settings.UnlockLevels[i]); }
            for (int i = 0; i < 7; i++) Set(parameterClone, 29 + i, settings.StatModifiers[i]);
            int lastParameter = catalog.Parameters.Rows.FindLastIndex(r => r.Name == "CHARA_PARAM_INFO");
            var marker = catalog.Parameters.Rows.First(r => r.Name == "CHARA_PARAM_INFO_LIST_BEG");
            Set(marker, 0, marker.Int(0) + 1); catalog.Parameters.Rows.Insert(lastParameter + 1, parameterClone);
            var parameters = catalog.Parameters.Rows.Where(r => r.Name == "CHARA_PARAM_INFO").ToArray();
            var sort = catalog.Parameters.Rows.Where(r => r.Name == "__SORT_INDEX").ToArray();
            if (sort.Length != parameters.Length - 1) throw new InvalidDataException("Unsupported parameter sort table.");
            int insertion = parameters.Take(parameters.Length - 1).Count(r => r.UInt(0) < allocated[1]);
            var newSort = sort[0].Clone(); Set(newSort, 0, parameters.Length - 1);
            int at = insertion == sort.Length ? catalog.Parameters.Rows.IndexOf(sort.Last()) + 1 : catalog.Parameters.Rows.IndexOf(sort[insertion]);
            catalog.Parameters.Rows.Insert(at, newSort);
            result.ParameterMap[parameter.UInt(0)] = allocated[1];
            occupied.UnionWith(allocated);
            foreach (var variant in settings.Variants ?? new List<CharacterTechniqueSettings>()) {
                if (result.ParameterMap.ContainsKey(variant.parameterId)) continue;
                var original = Single(catalog.Parameters, "CHARA_PARAM_INFO", variant.parameterId);
                if (original.UInt(1) != source.UInt(0) || variant.skills?.Length != 9 || variant.levels?.Length != 9)
                    throw new InvalidDataException("Invalid additional technique variant.");
                uint newId = G4TextureParser.Crc32("pc_para_" + name + "_" + variant.parameterId.ToString("X8"));
                if (newId == 0 || !occupied.Add(newId))
                    throw new InvalidDataException("固定技能表編號衝突：" + name + "。匯出已停止，不會自動改號。");
                var additional = original.Clone(); Set(additional, 0, newId); Set(additional, 1, allocated[0]);
                Set(additional, 2, settings.Affinity); Set(additional, 3, settings.MainPosition); Set(additional, 4, settings.SubPosition);
                for (int slot = 0; slot < 9; slot++) {
                    if (variant.skills[slot] != 0 && !catalog.Skills.ContainsKey(variant.skills[slot]) || variant.levels[slot] < 0 || variant.levels[slot] > 99)
                        throw new InvalidDataException("Invalid additional variant skill/level.");
                    Set(additional, 11 + slot * 2, variant.skills[slot]); Set(additional, 12 + slot * 2, variant.levels[slot]);
                }
                AppendParameter(catalog.Parameters, additional); result.ParameterMap[variant.parameterId] = newId;
            }
            foreach (var variant in settings.Variants ?? new List<CharacterTechniqueSettings>()) {
                if (!variant.hasBuildType) continue;
                if (variant.buildType < 0 || variant.buildType > 6) throw new InvalidDataException("Invalid build type.");
                var createdParameter = Single(catalog.Parameters, "CHARA_PARAM_INFO", result.ParameterMap[variant.parameterId]);
                Set(createdParameter, 5, variant.buildType);
            }
            var modelClone = model.Clone(); Set(modelClone, 0, allocated[2]); modelClone.Values[10] = "_face/" + result.ModelRelativePath.Replace('\\', '/');
            catalog.Models.Insert("CHARA_MODEL_INFO_LIST_BEG", "CHARA_MODEL_INFO_LIST_END", modelClone);
            foreach (var pair in catalog.TextTables)
            {
                token.ThrowIfCancellationRequested(); var table = pair.Value; string filename = Path.GetFileName(pair.Key);
                bool description = filename == "chara_description_text.cfg.bin";
                int[] fields = description ? new[] { 19 } : filename == "chara_text_roma.cfg.bin" ? new[] { 3 } : new[] { 3, 4, 5 };
                var appended = new List<CfgTable.Row>();
                foreach (int field in fields)
                {
                    string type = description ? "TEXT_INFO" : "NOUN_INFO";
                    var original = table.Rows.FirstOrDefault(r => r.Name == type && r.UInt(0) == source.UInt(field) && (description || r.Int(1) == 0));
                    if (original == null) throw new InvalidDataException(pair.Key + ": missing template text " + source.UInt(field));
                    var row = original.Clone(); Set(row, 0, description ? allocated[6] : allocated[field]);
                    if (!description) row.Values[5] = settings.DisplayName;
                    appended.Add(row);
                }
                table.Insert(description ? "TEXT_INFO_BEGIN" : "NOUN_INFO_BEGIN", description ? "TEXT_INFO_END" : "NOUN_INFO_END", appended.ToArray());
            }
            if (settings.AddFreeShopItem) AddShop(catalog, allocated[7], parameter, allocated[1]);
            foreach (var table in all)
            {
                byte[] bytes = table.Write();
                result.Files.Add(Path.Combine("common", table.Source.Substring(catalog.Common.Length + 1)), bytes);
            }
            var baseCheck = CfgTable.Read(result.Files[Path.Combine("common", catalog.Base.Source.Substring(catalog.Common.Length + 1))], "base output");
            if (Single(baseCheck, "CHARA_BASE_INFO", allocated[0]).UInt(6) != allocated[2]) throw new InvalidDataException("Character model reference mismatch.");
            var paramCheck = CfgTable.Read(result.Files[Path.Combine("common", catalog.Parameters.Source.Substring(catalog.Common.Length + 1))], "parameter output");
            var restored = Single(paramCheck, "CHARA_PARAM_INFO", allocated[1]);
            if (restored.UInt(1) != allocated[0]) throw new InvalidDataException("Parameter/base reference mismatch.");
            var restoredParameters = paramCheck.Rows.Where(r => r.Name == "CHARA_PARAM_INFO").ToArray();
            var restoredSort = paramCheck.Rows.Where(r => r.Name == "__SORT_INDEX").Select(r => checked((int)r.Int(0))).ToArray();
            if (restoredSort.Length != restoredParameters.Length || !restoredSort.OrderBy(i => i).SequenceEqual(Enumerable.Range(0, restoredParameters.Length)) ||
                !restoredSort.Select(i => restoredParameters[i].UInt(0)).SequenceEqual(restoredParameters.Select(r => r.UInt(0)).OrderBy(id => id))) throw new InvalidDataException("Parameter sort-index validation failed.");
            for (int i = 0; i < 7; i++) if (restored.Int(29 + i) != settings.StatModifiers[i]) throw new InvalidDataException("Stat modifier roundtrip mismatch.");
            for (int i = 0; i < 9; i++) if (restored.UInt(11 + 2 * i) != settings.Skills[i] || restored.Int(12 + 2 * i) != settings.UnlockLevels[i]) throw new InvalidDataException("Skill roundtrip mismatch.");
            if (settings.AddFreeShopItem) {
            var shopCheck = CfgTable.Read(result.Files[Path.Combine("common", catalog.Shop.Source.Substring(catalog.Common.Length + 1))], "shop output");
            var shopItem = Single(shopCheck, "SHOP_INFO_ITEM", allocated[7]);
            if (shopItem.UInt(24) != allocated[1]) throw new InvalidDataException("Shop/parameter reference mismatch.");
            var shopBlock = shopCheck.Rows.Skip(shopCheck.Rows.IndexOf(shopItem) + 1).TakeWhile(r => r.Name != "SHOP_INFO_ITEM" && r.Name != "SHOP_INFO_ITEM_LIST_END").ToArray();
            if (shopBlock.Count(r => r.Name == "SHOP_INFO_ITEM_CONSUME") != 1 || shopBlock.Single(r => r.Name == "SHOP_INFO_ITEM_CONSUME").Int(2) != 0 || shopBlock.Single(r => r.Name == "SHOP_INFO_ITEM_CONSUME").UInt(0) != FreeSoulParameter(catalog) || shopBlock.Any(r => r.Name == "SHOP_INFO_ITEM_CONDITION")) throw new InvalidDataException("Free shop item validation failed.");
            }
            string portraitDirectory = Path.Combine(dx11, "menu", "200_icon", "10_icon_chr", "face");
            result.SourcePortraitStem = source.Text(1);
            result.PortraitTemplate = Path.Combine(portraitDirectory, result.SourcePortraitStem + "_l.g4tx");
            if (!File.Exists(result.PortraitTemplate)) throw new InvalidDataException("範本缺少原生頭像：" + result.PortraitTemplate);
            result.SourceHashes.Add(result.PortraitTemplate, RawHeadAsset.Digest(File.ReadAllBytes(result.PortraitTemplate)));
            return result;
        }
        internal static uint Allocate(string key, HashSet<uint> occupied)
        {
            for (int i = 0; i < 1000000; i++) {
                uint id = G4TextureParser.Crc32(i == 0 ? key : key + "_" + i);
                if (id != 0 && occupied.Add(id)) return id;
            }
            throw new InvalidDataException("No available identifiers.");
        }
        static void AppendParameter(CfgTable table, CfgTable.Row row)
        {
            int last = table.Rows.FindLastIndex(r => r.Name == "CHARA_PARAM_INFO");
            var marker = table.Rows.First(r => r.Name == "CHARA_PARAM_INFO_LIST_BEG");
            Set(marker, 0, marker.Int(0) + 1); table.Rows.Insert(last + 1, row);
            var parameters = table.Rows.Where(r => r.Name == "CHARA_PARAM_INFO").ToArray();
            var sort = table.Rows.Where(r => r.Name == "__SORT_INDEX").ToArray();
            int insertion = parameters.Take(parameters.Length - 1).Count(r => r.UInt(0) < row.UInt(0));
            var next = sort[0].Clone(); Set(next, 0, parameters.Length - 1);
            int at = insertion == sort.Length ? table.Rows.IndexOf(sort.Last()) + 1 : table.Rows.IndexOf(sort[insertion]);
            table.Rows.Insert(at, next);
        }
        internal static uint FreeSoulParameter(CharacterCatalog c)
        {
            // Slot 00 is the generic avatar template. Slot 01 is the player's avatar,
            // whose display name is resolved from the game save rather than a fixed text label.
            var avatar = c.Base.Rows.SingleOrDefault(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == "edit_avatar01")
                ?? throw new InvalidDataException("Missing player avatar soul reference.");
            return c.Parameters.Rows.Single(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(1) == avatar.UInt(0)).UInt(0);
        }
        static void SetFreeConsume(CfgTable.Row consume, uint soul)
        {
            if (consume.Values.Length != 3 || consume.Types.Any(t => t != 1))
                throw new InvalidDataException("Unsupported shop consume schema.");
            Set(consume, 0, soul); Set(consume, 1, 8); Set(consume, 2, 0);
        }
        internal static void NormalizeFreeShopBlock(CharacterCatalog c, CfgTable.Row item)
        {
            var shop = c.Shop; uint soul = FreeSoulParameter(c);
            int start = shop.Rows.IndexOf(item);
            var block = shop.Rows.Skip(start + 1).TakeWhile(r => r.Name != "SHOP_INFO_ITEM" && r.Name != "SHOP_INFO_ITEM_LIST_END").ToArray();
            var marker = block.Single(r => r.Name == "SHOP_INFO_ITEM_CONSUME_LIST_BEG");
            var consume = block.FirstOrDefault(r => r.Name == "SHOP_INFO_ITEM_CONSUME");
            if (consume == null) {
                consume = shop.Rows.FirstOrDefault(r => r.Name == "SHOP_INFO_ITEM_CONSUME")?.Clone()
                    ?? throw new InvalidDataException("No shop consume template available.");
                shop.Rows.Insert(shop.Rows.IndexOf(marker) + 1, consume);
            }
            Set(marker, 0, 1); SetFreeConsume(consume, soul);
            foreach (var row in block) {
                if (row.Name == "SHOP_INFO_ITEM_CONSUME" && row != consume) shop.Rows.Remove(row);
                if (row.Name == "SHOP_INFO_ITEM_CONDITION_LIST_BEG") Set(row, 0, 0);
                if (row.Name == "SHOP_INFO_ITEM_CONDITION") shop.Rows.Remove(row);
            }
        }
        internal static void RepairGeneratedFreeShopEntries(CharacterCatalog c)
        {
            var characters = c.Base.Rows.Where(r => r.Name == "CHARA_BASE_INFO").ToDictionary(r => r.UInt(0), r => r.Text(1));
            var parameters = c.Parameters.Rows.Where(r => r.Name == "CHARA_PARAM_INFO").ToDictionary(r => r.UInt(0), r => r.UInt(1));
            foreach (var item in c.Shop.Rows.Where(r => r.Name == "SHOP_INFO_ITEM" && r.Values.Length == 27).ToArray()) {
                if (!parameters.TryGetValue(item.UInt(24), out uint baseId) || !characters.TryGetValue(baseId, out string name)) continue;
                bool generated = name.StartsWith("c9", StringComparison.Ordinal) && item.UInt(0) == G4TextureParser.Crc32("shop_" + name);
                string key = "shop_free_" + name + "_" + item.UInt(24).ToString("X8");
                for (int suffix = 0; !generated && suffix < 32; suffix++)
                    generated = item.UInt(0) == G4TextureParser.Crc32(suffix == 0 ? key : key + "_" + suffix);
                if (generated) NormalizeFreeShopBlock(c, item);
            }
        }
        internal static void AddShop(CharacterCatalog c, uint itemId, CfgTable.Row parameter, uint parameterId)
        {
            var shop = c.Shop;
            var playable = new HashSet<uint>(c.Parameters.Rows.Where(r => r.Name == "CHARA_PARAM_INFO").Select(r => r.UInt(0)));
            Func<CfgTable.Row, bool> valid = r =>
            {
                if (r.Name != "SHOP_INFO_ITEM" || r.Values.Length != 27 || !playable.Contains(r.UInt(24))) return false;
                var names = shop.Rows.Skip(shop.Rows.IndexOf(r) + 1).TakeWhile(x => x.Name != "SHOP_INFO_ITEM" && x.Name != "SHOP_INFO_ITEM_LIST_END").Select(x => x.Name).ToArray();
                return names.Contains("SHOP_INFO_ITEM_CONSUME") && names.Contains("SHOP_INFO_ITEM_CONDITION_LIST_BEG");
            };
            var source = shop.Rows.FirstOrDefault(r => valid(r) && r.UInt(24) == parameter.UInt(0)) ?? shop.Rows.FirstOrDefault(valid);
            if (source == null) throw new InvalidDataException("No valid character shop template.");
            int start = shop.Rows.IndexOf(source), begin = shop.Rows.FindLastIndex(start, r => r.Name == "SHOP_INFO_ITEM_LIST_BEG" || r.Name == "SHOP_INFO_ITEM_LIST_END");
            int end = shop.Rows.FindIndex(start + 1, r => r.Name == "SHOP_INFO_ITEM_LIST_END");
            if (begin < 0 || end < 0 || shop.Rows[begin].Name != "SHOP_INFO_ITEM_LIST_BEG" || shop.Rows[begin].Int(0) != shop.Rows.Skip(begin + 1).Take(end - begin - 1).Count(r => r.Name == "SHOP_INFO_ITEM")) throw new InvalidDataException("Shop list count mismatch.");
            var block = new List<CfgTable.Row>(); bool consume = false, conditionMarker = false;
            foreach (var row in shop.Rows.Skip(start).Take(end - start).TakeWhile(r => r == source || r.Name != "SHOP_INFO_ITEM"))
            {
                var copy = row.Clone();
                if (row == source) { Set(copy, 0, itemId); Set(copy, 24, parameterId); Set(copy, 25, parameter.Int(41)); if (source.UInt(24) != parameter.UInt(0)) Set(copy, 26, 0); }
                else if (copy.Types.Length > 0 && copy.Types[0] == 1 && copy.UInt(0) == source.UInt(24)) Set(copy, 0, parameterId);
                if (copy.Name == "SHOP_INFO_ITEM_CONSUME_LIST_BEG") Set(copy, 0, 1);
                if (copy.Name == "SHOP_INFO_ITEM_CONSUME") {
                    if (consume) continue;
                    SetFreeConsume(copy, FreeSoulParameter(c)); consume = true;
                }
                if (copy.Name == "SHOP_INFO_ITEM_CONDITION_LIST_BEG") { Set(copy, 0, 0); conditionMarker = true; }
                if (copy.Name == "SHOP_INFO_ITEM_CONDITION") continue;
                block.Add(copy);
            }
            if (!consume || !conditionMarker) throw new InvalidDataException("Shop template lacks a price/condition list.");
            Set(shop.Rows[begin], 0, shop.Rows[begin].Int(0) + 1); shop.Rows.InsertRange(end, block);
        }
    }
}
