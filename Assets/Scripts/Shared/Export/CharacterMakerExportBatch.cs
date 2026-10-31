using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using G4Composition;
using G4Runtime;
using UnityEngine;

// One cumulative catalog per batch. Nothing is copied to the destination until every
// character, model and table has been built successfully.
public static class CharacterMakerExportBatch
{
    static CharacterMakerDocument Copy(CharacterMakerDocument value) => JsonUtility.FromJson<CharacterMakerDocument>(JsonUtility.ToJson(value));
    static IEnumerable<CfgTable> Tables(CharacterCatalog c) => new[] { c.Base, c.Parameters, c.Models, c.Shop }.Concat(c.TextTables.Values);
    static void Set(CfgTable.Row row, int field, long value) => row.Values[field] = unchecked((long)(int)value);
    static string Relative(CharacterCatalog c, string path) => Path.Combine("common", path.Substring(c.Common.Length + 1));

    // Length framing prevents ambiguous concatenations. Name casing follows Windows mod folders.
    public static string NamespacedKey(string modName, string characterKey)
    {
        if (string.IsNullOrWhiteSpace(modName)) return characterKey;
        string name = modName.Trim().Normalize(System.Text.NormalizationForm.FormC).ToUpperInvariant();
        return name.Length + ":" + name + ":" + characterKey;
    }

    public static string[] Export(IEnumerable<CharacterMakerDocument> source, string destination, string modNamespace = null)
    {
        if (!DumpDataManager.ValidateSavedPath()) throw new InvalidDataException(DumpDataManager.LastError);
        var docs = source.Select(Copy).ToArray();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var newKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var doc in docs) {
            if (doc.IsNew) {
                ValidateNew(doc);
                if (!newKeys.Add(doc.newCharacterKey)) throw new InvalidDataException("同一批資料包含重複的新角色存檔：" + doc.newCharacterKey);
            } else if (string.IsNullOrWhiteSpace(doc.targetId) || !targets.Add(doc.targetId))
                throw new InvalidDataException("替換目標為空或重複：" + doc.targetId);
        }
        if (docs.Length == 0) throw new InvalidDataException("沒有要匯出的角色。");
        destination = Path.GetFullPath(destination);
        var catalog = CharacterCatalog.Read(DumpDataManager.CommonPath, CancellationToken.None);
        // Retain previously exported characters and unrelated settings already in this data folder.
        CfgTable Existing(CfgTable table) {
            string path = Path.Combine(destination, Relative(catalog, table.Source));
            return File.Exists(path) ? CfgTable.Read(File.ReadAllBytes(path), table.Source) : table;
        }
        catalog.Base = Existing(catalog.Base); catalog.Parameters = Existing(catalog.Parameters);
        catalog.Models = Existing(catalog.Models); catalog.Shop = Existing(catalog.Shop);
        foreach (string key in catalog.TextTables.Keys.ToArray()) catalog.TextTables[key] = Existing(catalog.TextTables[key]);
        NewCharacterWriter.RepairGeneratedFreeShopEntries(catalog);
        var geometry = new List<CharacterMakerBatchExporter.BuiltCharacter>();
        var settings = new List<CharacterMakerDocument>();
        var exporter = new CharacterMakerBatchExporter();
        foreach (var original in docs) {
            var working = Copy(original);
            if (original.IsNew) {
                string template = original.TemplateId;
                var character = catalog.Templates.SingleOrDefault(r => r.Text(1) == template)
                    ?? throw new InvalidDataException("無法使用此新增角色範本：" + template);
                var variant = original.identity.techniques.Single(t => t.parameterId == original.identity.techniqueParameterId);
                var parameter = catalog.Parameters.Rows.Single(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(0) == variant.parameterId && r.UInt(1) == character.UInt(0));
                var primary = original.identity.locales.FirstOrDefault(t => t.locale == "en" && !string.IsNullOrWhiteSpace(t.fullName))
                    ?? original.identity.locales.First(t => !string.IsNullOrWhiteSpace(t.fullName));
                var request = new ExportRequest {
                    Document = new CompositionDocument { Id = NamespacedKey(modNamespace, original.newCharacterKey) }, TargetRelativePath = catalog.Face(character),
                    Character = new NewCharacterSettings {
                        TemplateBaseId = character.UInt(0), TemplateParameterId = variant.parameterId,
                        DisplayName = primary.fullName, Affinity = original.identity.affinity,
                        MainPosition = original.identity.position, SubPosition = original.identity.subPosition,
                        StatModifiers = Enumerable.Range(0, 7).Select(i => checked((int)parameter.Int(29 + i))).ToArray(),
                        Skills = variant.skills, UnlockLevels = variant.levels,
                        Variants = original.identity.techniques, AddFreeShopItem = original.freeSoulInShop
                    }
                };
                var created = new NewCharacterWriter().Build(catalog, request, DumpDataManager.Dx11Path, CancellationToken.None);
                string modelOutput = Path.Combine(destination, "common", "chr", "_face", created.ModelRelativePath);
                string portraitOutput = Path.Combine(destination, "dx11", "menu", "200_icon", "10_icon_chr", "face", created.InternalName + "_l.g4tx");
                if (File.Exists(modelOutput) || File.Exists(portraitOutput))
                    throw new InvalidDataException("新角色的輸出路徑已存在但無法從資料表確認，已停止以避免覆蓋：" + created.InternalName);
                // Compose against the native template, then write the result under the new model path.
                working.isNewCharacter = false; working.targetId = template;
                var built = exporter.Build(working);
                working.targetId = created.InternalName;
                working.identity.sourceId = created.InternalName;
                working.identity.techniqueParameterId = created.ParameterMap[variant.parameterId];
                foreach (var v in working.identity.techniques) v.parameterId = created.ParameterMap[v.parameterId];
                foreach (var text in working.identity.locales) {
                    text.originalFullName = text.originalShortName = text.originalShirtName = primary.fullName;
                    if (string.IsNullOrWhiteSpace(text.fullName)) text.fullName = primary.fullName;
                    if (string.IsNullOrWhiteSpace(text.shortName)) text.shortName = text.fullName;
                    if (string.IsNullOrWhiteSpace(text.shirtName)) text.shirtName = primary.shirtName;
                    text.japaneseName = text.locale == "ja" ? text.fullName : null;
                }
                if (string.IsNullOrWhiteSpace(working.voiceCharacterId) && string.IsNullOrWhiteSpace(working.voiceAvatarCode)) working.voiceCharacterId = template;
                built.Document.targetId = created.InternalName; built.TargetRelativePath = created.ModelRelativePath;
                geometry.Add(built); settings.Add(working);
            } else {
                geometry.Add(exporter.Build(working)); settings.Add(working);
                if (original.freeSoulInShop) AddReplacementSoul(catalog, original);
            }
        }
        string stage = Path.Combine(Path.GetDirectoryName(destination), ".cms-" + Guid.NewGuid().ToString("N").Substring(0, 12));
        try {
            Directory.CreateDirectory(stage);
            CharacterMakerBatchExporter.WriteMergedCharacterSettings(settings, stage, catalog);
            foreach (var built in geometry) CharacterMakerBatchExporter.Write(built, stage);
            foreach (var table in Tables(catalog)) {
                byte[] bytes = table.Write(); CfgTable.Read(bytes, "batch settings validation");
                string path = Path.Combine(stage, Relative(catalog, table.Source));
                Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, bytes);
            }
            if (catalog.Base.Rows.Any(r => r.Name == "CHARA_BASE_INFO" && r.Text(1).StartsWith("c9", StringComparison.Ordinal)))
                File.WriteAllBytes(Path.Combine(stage, "cpk_list.cfg.bin"), CharacterCpkRegistry.Build(CharacterCpkRegistry.FindSource(), destination, stage));
            foreach (string path in Directory.GetFiles(stage, "*", SearchOption.AllDirectories)) {
                string output = Path.Combine(destination, path.Substring(stage.Length + 1));
                Directory.CreateDirectory(Path.GetDirectoryName(output)); File.Copy(path, output, true);
            }
            return settings.Select(d => d.targetId).ToArray();
        } finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
    }

    public static void ValidateNew(CharacterMakerDocument doc)
    {
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(doc.newCharacterKey)) missing.Add("新角色識別碼（請重新儲存）");
        if (string.IsNullOrWhiteSpace(doc.templateId)) missing.Add("結構範本");
        if (string.IsNullOrWhiteSpace(doc.head?.sourceId)) missing.Add("頭型");
        if (string.IsNullOrWhiteSpace(doc.hair?.sourceId)) missing.Add("頭髮");
        if (doc.identity?.locales == null || !doc.identity.locales.Any(t => !string.IsNullOrWhiteSpace(t.fullName))) missing.Add("全名");
        if (doc.identity?.locales == null || !doc.identity.locales.Any(t => !string.IsNullOrWhiteSpace(t.shirtName))) missing.Add("制服名字");
        if (doc.identity?.techniques == null || !doc.identity.techniques.Any(t => t.parameterId == doc.identity.techniqueParameterId)) missing.Add("技能表");
        if (doc.identity != null && !string.Equals(doc.identity.sourceId, doc.templateId, StringComparison.OrdinalIgnoreCase)) missing.Add("人物資料與範本對應");
        if (string.IsNullOrWhiteSpace(doc.portraitG4txBase64)) missing.Add("人物圖示（請設定頭部後重新儲存）");
        if (missing.Count > 0) throw new InvalidDataException("新增角色缺少：" + string.Join("、", missing));
    }
    static void AddReplacementSoul(CharacterCatalog c, CharacterMakerDocument doc)
    {
        var character = c.Base.Rows.Single(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == doc.targetId);
        var parameter = c.Parameters.Rows.First(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(1) == character.UInt(0) &&
            (doc.identity == null || r.UInt(0) == doc.identity.techniqueParameterId));
        foreach (var item in c.Shop.Rows.Where(r => r.Name == "SHOP_INFO_ITEM" && r.Values.Length == 27 && r.UInt(24) == parameter.UInt(0))) {
            var block = c.Shop.Rows.Skip(c.Shop.Rows.IndexOf(item) + 1).TakeWhile(r => r.Name != "SHOP_INFO_ITEM" && r.Name != "SHOP_INFO_ITEM_LIST_END").ToArray();
            if ((block.Any(r => r.Name == "SHOP_INFO_ITEM_CONSUME" && r.Values.Length == 3 && r.Int(2) == 0) || block.Any(r => r.Name == "SHOP_INFO_ITEM_CONSUME_LIST_BEG" && r.Int(0) == 0)) && !block.Any(r => r.Name == "SHOP_INFO_ITEM_CONDITION")) { NewCharacterWriter.NormalizeFreeShopBlock(c, item); return; }
        }
        var occupied = new HashSet<uint>(Tables(c).SelectMany(t => t.Rows).SelectMany(r => r.Values.Select((v, i) => new { v, type = r.Types[i] })).Where(x => x.type == 1).Select(x => unchecked((uint)Convert.ToInt64(x.v))));
        NewCharacterWriter.AddShop(c, NewCharacterWriter.Allocate("shop_free_" + doc.targetId + "_" + parameter.UInt(0).ToString("X8"), occupied), parameter, parameter.UInt(0));
    }
}