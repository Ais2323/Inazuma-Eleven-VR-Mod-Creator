using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using G4Composition;
using G4Runtime;

public static partial class UniformMakerExporter
{
    public static void Export(IEnumerable<UniformMakerDocument> source, string output, string modName)
    {
        if (!DumpDataManager.ValidateSavedPath()) throw new InvalidDataException(DumpDataManager.LastError);
        var docs = source.ToArray();
        foreach (var doc in docs) doc.Validate();
        if (docs.Where(d => !d.isNewUniform).GroupBy(d => d.targetUniformId).Any(g => g.Count() > 1) ||
            docs.Where(d => d.isNewUniform).GroupBy(d => d.newUniformKey).Any(g => g.Count() > 1))
            throw new InvalidDataException("More than one save targets the same uniform.");
        output = Path.GetFullPath(output);
        string character = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
        string nativeParts = CharacterIdentityCatalog.Find(character, "chara_parts_");
        string nativeConfig = CharacterIdentityCatalog.Find(character, "uniform_config_");
        string partsOutput = Path.Combine(output, "common", "gamedata", "character", Path.GetFileName(nativeParts));
        string configOutput = Path.Combine(output, "common", "gamedata", "character", Path.GetFileName(nativeConfig));
        var parts = CfgTable.Read(File.ReadAllBytes(File.Exists(partsOutput) ? partsOutput : nativeParts), nativeParts);
        byte[] configBytes = File.ReadAllBytes(File.Exists(configOutput) ? configOutput : nativeConfig);
        var newIds = PrepareInventory(docs, output, modName, ref configBytes);
        var config = new AvatarRecipeParser.Rdbnp(configBytes, nativeConfig);
        var uniformList = config.GetList("m_UniformInfoList"); var modelList = config.GetList("m_UniformModelInfoList");
        var catalog = new UniformCatalog();
        var nativeRows = CfgTable.Read(File.ReadAllBytes(nativeParts), nativeParts);
        var nativeColorIds = new HashSet<uint>(nativeRows.Rows.Where(r => r.Name == "CHARA_PARTS_COLOR").Select(r => r.UInt(0)));
        var occupied = new HashSet<uint>(parts.Rows.Where(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" || r.Name == "CHARA_PARTS_GLOVE_MODEL").Select(r => r.UInt(0)));
        var converted = new Dictionary<string, byte[]>();
        var pending = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var replacementClothes = new Dictionary<uint, uint>();
        foreach (var doc in docs)
        {
            uint templateId = doc.isNewUniform ? doc.templateUniformId : doc.targetUniformId;
            uint targetId = doc.isNewUniform ? newIds[doc] : doc.targetUniformId;
            var entry = catalog.Entries.SingleOrDefault(e => e.Id == templateId)
                ?? throw new InvalidDataException("Unknown target uniform: " + doc.targetKey);
            int info = Enumerable.Range(0, uniformList.Count).Select(uniformList.Row).Single(r => config.U32(r) == targetId);
            uint span = config.U32(info + uniformList.Field("modelInfo"));
            foreach (var variant in ExpandBodyVariants(doc, catalog).Where(v => v.HasChanges).OrderBy(v => v.bodyAttribute))
            {
                int model = Enumerable.Range((int)(span & 0xffff), (int)(span >> 16)).Select(modelList.Row)
                    .Single(r => config.Byte(r + modelList.Field("typeId")) == variant.side);
                string key = CharacterMakerExportBatch.NamespacedKey(modName, "uniform:" + targetId + ":" + variant.side + ":" + variant.role + ":" + variant.bodyAttribute);
                uint Allocate(string value)
                {
                    uint id = G4TextureParser.Crc32Utf8(value);
                    if (id == 0 || !occupied.Add(id)) throw new InvalidDataException("Uniform resource ID collision: " + value);
                    return id;
                }
                if ((variant.portraitIcons?.Count ?? 0) > 0 || variant.clothesModelId != 0 || variant.armbandModelId != 0 || variant.skinModelId != 0 || variant.numberModelId != 0 || variant.nameModelId != 0 || variant.IsHidden(UniformPartKind.Name) ||
                    variant.IsHidden(UniformPartKind.Skin) || variant.IsHidden(UniformPartKind.Number) || variant.IsHidden(UniformPartKind.Armband) ||
                    variant.fbx != null || variant.textures.Any(t => t.part != UniformPartKind.Gloves))
                {
                    uint template = variant.clothesModelId != 0 ? variant.clothesModelId : entry.Models[variant.side, variant.role];
                    var templateModel = nativeRows.Rows.Single(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.UInt(0) == template);
                    string sourceName = templateModel.Text(1);
                    var targetModel = nativeRows.Rows.Single(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.UInt(0) == entry.Models[variant.side, variant.role]);
                    var targetInfo = Referenced(nativeRows, targetModel, "CHARA_PARTS_CLOTHES_INFO");
                    uint previousId = config.U32(model + variant.role * 4);
                    var previousModel = parts.Rows.Single(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.UInt(0) == previousId);
                    string targetName = "um_" + Digest(key + ":clothes");
                    uint mainId = 0;
                    var madeStyles = new Dictionary<string, uint>();
                    foreach (string suffix in new[] { "", "_slv", "_blyR" })
                    {
                        var sourceModel = nativeRows.Rows.FirstOrDefault(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.Text(1) == sourceName + suffix);
                        if (variant.fbx != null) sourceModel = templateModel;
                        if (sourceModel == null && variant.bodyAttribute >= 0) sourceModel = templateModel;
                        if (sourceModel == null) continue;
                        uint id = Allocate(targetName + suffix);
                        if (suffix == "") mainId = id;
                        madeStyles.Add(suffix, id);
                        var sourceInfo = Referenced(nativeRows, sourceModel, "CHARA_PARTS_CLOTHES_INFO");
                        if (variant.bodyAttribute >= 0 && NativeBodyTypes.ClothesRow(sourceInfo, catalog.BodyByAttribute(variant.bodyAttribute)) == null)
                            sourceInfo = Referenced(nativeRows, templateModel, "CHARA_PARTS_CLOTHES_INFO");
                        var rows = sourceInfo.Select(r => r.Clone()).ToArray();
                        if (variant.bodyAttribute >= 0)
                        {
                            var body = catalog.BodyByAttribute(variant.bodyAttribute);
                            var selectedSource = NativeBodyTypes.ClothesRow(sourceInfo, body);
                            if (selectedSource == null) throw new InvalidDataException("Selected clothes do not support body attribute " + variant.bodyAttribute);
                            var scoped = selectedSource.Clone(); scoped.Values[2] = (long)variant.bodyAttribute;
                            for (int field = 0; field < scoped.Values.Length; field++)
                                if (string.IsNullOrWhiteSpace(scoped.Text(field)) && !string.IsNullOrWhiteSpace(sourceInfo[0].Text(field))) Text(scoped, field, sourceInfo[0].Text(field));
                            var previousStyle = parts.Rows.FirstOrDefault(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.Text(1) == previousModel.Text(1) + suffix) ?? previousModel;
                            var previousRows = Referenced(parts, previousStyle, "CHARA_PARTS_CLOTHES_INFO");
                            var merged = previousRows.Select(r => r.Clone()).ToList();
                            foreach (var inherited in merged) for (int field = 0; field < inherited.Values.Length; field++)
                                if (string.IsNullOrWhiteSpace(inherited.Text(field)) && !string.IsNullOrWhiteSpace(previousRows[0].Text(field))) Text(inherited, field, previousRows[0].Text(field));
                            int index = merged.FindIndex(r => r.Int(2) == variant.bodyAttribute);
                            if (index < 0) merged.Add(scoped); else merged[index] = scoped;
                            rows = merged.ToArray();
                        }
                        for (int i = 0; i < rows.Length; i++)
                        {
                            var row = rows[i];
                            if (variant.bodyAttribute >= 0 && row.Int(2) != variant.bodyAttribute) continue;
                            string Read(int field) => row.Text(field) ?? sourceInfo[0].Text(field);
                            void Accessory(int modelField, int textureField, UniformPartKind kind, uint selectedId)
                            {
                                var body = catalog.BodyByAttribute((int)row.Int(2));
                                if (selectedId != 0) {
                                    var selectedModel = nativeRows.Rows.Single(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.UInt(0) == selectedId);
                                    var refs = Referenced(nativeRows, selectedModel, "CHARA_PARTS_CLOTHES_INFO");
                                    var selectedPart = NativeBodyTypes.ClothesRow(refs, body);
                                    if (selectedPart == null) throw new InvalidDataException("Accessory is not available for this body.");
                                    Text(row, modelField, selectedPart.Text(modelField) ?? refs[0].Text(modelField));
                                    Text(row, textureField, selectedPart.Text(textureField) ?? refs[0].Text(textureField));
                                    if (kind == UniformPartKind.Number) { row.Types[5] = selectedPart.Types[5]; row.Values[5] = selectedPart.Values[5]; }
                                }
                                if (!variant.IsHidden(kind)) return;
                                string hiddenMesh = row.Text(modelField) ?? Read(modelField), hiddenTexture = row.Text(textureField) ?? Read(textureField);
                                if (string.IsNullOrWhiteSpace(hiddenMesh) || string.IsNullOrWhiteSpace(hiddenTexture)) {
                                    var fallback = catalog.OptionsFor(kind, body).FirstOrDefault();
                                    var part = fallback == null ? null : NativeUniformFashion.Resolve(NativeUniformFashion.ModelName(fallback.Id), body);
                                    hiddenMesh = kind == UniformPartKind.Skin ? part?.Skin : kind == UniformPartKind.Number ? part?.Number : part?.Armband;
                                    hiddenTexture = kind == UniformPartKind.Skin ? part?.SkinTexture : kind == UniformPartKind.Number ? part?.NumberTexture : part?.ArmbandTexture;
                                }
                                if (string.IsNullOrWhiteSpace(hiddenMesh) || string.IsNullOrWhiteSpace(hiddenTexture)) {
                                    row.Types[modelField] = row.Types[textureField] = 1; row.Values[modelField] = row.Values[textureField] = 0L; return;
                                }
                                var hidden = BuildEmptyResource(hiddenMesh, hiddenTexture, kind, pending); Text(row, modelField, hidden[0]); Text(row, textureField, hidden[1]);
                            }
                            Accessory(6, 7, UniformPartKind.Skin, variant.skinModelId);
                            bool customLabels = variant.numberModelId != 0 || variant.NameModelId != 0 || variant.IsHidden(UniformPartKind.Number) || variant.IsHidden(UniformPartKind.Name) ||
                                variant.textures.Any(t => t.part == UniformPartKind.Number || t.part == UniformPartKind.Name);
                            if (customLabels) {
                                var labels = BuildLabels(Read(3), Read(4), variant, catalog.BodyByAttribute((int)row.Int(2)), key + suffix + ":" + row.Int(2), converted, pending);
                                if (labels != null) { Text(row, 3, labels[0]); Text(row, 4, labels[1]); }
                                if (variant.numberModelId != 0) {
                                    var numberModel = nativeRows.Rows.Single(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.UInt(0) == variant.numberModelId);
                                    var numberRows = Referenced(nativeRows, numberModel, "CHARA_PARTS_CLOTHES_INFO");
                                    var numberRow = numberRows.FirstOrDefault(r => r.Int(2) == row.Int(2)) ?? numberRows.FirstOrDefault(r => r.Int(2) == catalog.BodyByAttribute((int)row.Int(2)).Int(6)) ?? numberRows[0];
                                    row.Types[5] = numberRow.Types[5]; row.Values[5] = numberRow.Values[5];
                                }
                            }
                            Accessory(8, 9, UniformPartKind.Armband, variant.armbandModelId);
                            void Build(int modelField, int textureField, UniformPartKind kind, bool split)
                            {
                                string mesh = row.Text(modelField) ?? Read(modelField), texture = row.Text(textureField) ?? Read(textureField);
                                if (string.IsNullOrWhiteSpace(mesh) || string.IsNullOrWhiteSpace(texture)) return;
                                string ownerName = null;
                                string modelName = null;
                                if (modelField == 0)
                                {
                                    var targetRow = targetInfo.FirstOrDefault(r => r.Int(2) == row.Int(2)) ?? targetInfo[0];
                                    ownerName = Path.GetFileName(targetRow.Text(1) ?? targetInfo[0].Text(1));
                                    modelName = Path.GetFileName(targetRow.Text(0) ?? targetInfo[0].Text(0));
                                    if ((variant.portraitIcons?.Count ?? 0) > 0) {
                                        string textureStem = WritePortraitFiles(variant, catalog.BodyByAttribute((int)row.Int(2)), sourceModel.UInt(0), targetModel.UInt(0),
                                            key + suffix + ":" + row.Int(2), suffix == "_slv" ? 1 : 0, converted, pending);
                                        ownerName = textureStem + ".g4tx";
                                        modelName = "u" + Digest(key + suffix + ":" + row.Int(2) + "|portrait") + ".g4md";
                                    }
                                }
                                var paths = BuildResource(mesh, texture, kind, split, variant, key, converted, pending, catalog, ownerName, modelName);
                                Text(row, modelField, paths[0]); Text(row, textureField, paths[1]);
                                uint oldHash = G4TextureParser.Crc32(Path.GetFileNameWithoutExtension(texture));
                                uint newHash = G4TextureParser.Crc32(Path.GetFileNameWithoutExtension(paths[1]));
                                for (int field = 3; field < row.Values.Length; field++)
                                    if (row.Types[field] == 1 && row.UInt(field) == oldHash &&
                                        !((variant.portraitIcons?.Count ?? 0) > 0 && nativeColorIds.Contains(row.UInt(field))))
                                        row.Values[field] = unchecked((long)(int)newHash);
                            }
                            Build(0, 1, UniformPartKind.Upper, true);
                            if (!variant.IsHidden(UniformPartKind.Skin)) Build(6, 7, UniformPartKind.Skin, false);
                            if (!customLabels) Build(3, 4, UniformPartKind.Number, false);
                            if (!variant.IsHidden(UniformPartKind.Armband)) Build(8, 9, UniformPartKind.Armband, false);
                        }
                        Append(parts, sourceModel, id, targetName + suffix, "CHARA_PARTS_CLOTHES_INFO", rows);
                    }
                    if (mainId == 0) throw new InvalidDataException("Cannot resolve selected uniform model.");
                    var variantMap = new Dictionary<uint, uint>();
                    foreach (string suffix in new[] { "", "_slv", "_blyR" })
                    {
                        var original = nativeRows.Rows.FirstOrDefault(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.Text(1) == targetModel.Text(1) + suffix);
                        if (original == null) continue;
                        uint oldId = original.UInt(0), newId = madeStyles.TryGetValue(suffix, out uint style) ? style : mainId;
                        variantMap[oldId] = newId;
                        var previousStyle = parts.Rows.FirstOrDefault(r => r.Name == "CHARA_PARTS_CLOTHES_MODEL" && r.Text(1) == previousModel.Text(1) + suffix);
                        if (previousStyle != null) variantMap[previousStyle.UInt(0)] = newId;
                        if (!doc.isNewUniform && replacementClothes.TryGetValue(oldId, out uint prior) && prior != newId &&
                            !(variant.bodyAttribute >= 0 && (prior == previousId || previousStyle?.UInt(0) == prior)))
                            throw new InvalidDataException("Two edited uniform variants share the same native clothing ID.");
                        if (!doc.isNewUniform) replacementClothes[oldId] = newId;
                    }
                    // Match play can choose specialized models directly from this table.
                    foreach (int field in new[] { 0, 16, 24, 32, 40, 48, 56, 64 })
                    {
                        int offset = model + field + variant.role * 4;
                        if (variantMap.TryGetValue(config.U32(offset), out uint replacement)) Put(configBytes, offset, replacement);
                    }
                }
                if (variant.role == 1 && (variant.gloveModelId != 0 || variant.IsHidden(UniformPartKind.Gloves) || variant.textures.Any(t => t.part == UniformPartKind.Gloves)))
                {
                    uint template = variant.gloveModelId != 0 ? variant.gloveModelId : entry.Gloves[variant.side];
                    var glove = nativeRows.Rows.Single(r => r.Name == "CHARA_PARTS_GLOVE_MODEL" && r.UInt(0) == template);
                    var sourceInfo = Referenced(nativeRows, glove, "CHARA_PARTS_GLOVE_INFO");
                    var rows = sourceInfo.Select(r => r.Clone()).ToArray();
                    int scopedFamily = variant.bodyAttribute < 0 ? -1 : (int)catalog.BodyByAttribute(variant.bodyAttribute).Int(5) % 4;
                    if (scopedFamily >= 0)
                    {
                        uint previousGlove = config.U32(model + modelList.Field("gloveModelIdCrc"));
                        var previous = parts.Rows.FirstOrDefault(r => r.Name == "CHARA_PARTS_GLOVE_MODEL" && r.UInt(0) == previousGlove);
                        if (previous != null)
                        {
                            var merged = Referenced(parts, previous, "CHARA_PARTS_GLOVE_INFO").Select(r => r.Clone()).ToList();
                            var selectedGlove = (sourceInfo.FirstOrDefault(r => r.Int(2) == scopedFamily) ?? sourceInfo[0]).Clone(); selectedGlove.Values[2] = (long)scopedFamily;
                            int index = merged.FindIndex(r => r.Int(2) == scopedFamily); if (index < 0) merged.Add(selectedGlove); else merged[index] = selectedGlove;
                            rows = merged.ToArray();
                        }
                    }
                    for (int i = 0; i < rows.Length; i++)
                    {
                        if (scopedFamily >= 0 && rows[i].Int(2) != scopedFamily) continue;
                        string mesh = rows[i].Text(0) ?? sourceInfo[0].Text(0), texture = rows[i].Text(1) ?? sourceInfo[0].Text(1);
                        var paths = variant.IsHidden(UniformPartKind.Gloves) ? BuildEmptyResource(mesh, texture, UniformPartKind.Gloves, pending) :
                            BuildResource(mesh, texture, UniformPartKind.Gloves, false, variant, key, converted, pending, catalog);
                        Text(rows[i], 0, paths[0]); Text(rows[i], 1, paths[1]);
                    }
                    uint id = Allocate(key + ":gloves");
                    Append(parts, glove, id, null, "CHARA_PARTS_GLOVE_INFO", rows);
                    Put(configBytes, model + modelList.Field("gloveModelIdCrc"), id);
                }
            }
        }
        CoalesceResources(parts, pending);
        byte[] partBytes = parts.Write(); CfgTable.Read(partBytes, "Uniform export validation");
        foreach (var file in pending)
        {
            string path = Path.Combine(output, file.Key); Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, file.Value);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(partsOutput)); File.WriteAllBytes(partsOutput, partBytes); File.WriteAllBytes(configOutput, configBytes);
        // Default appearances store clothing IDs directly, independently of match kits.
        // Start from the character batch's table so mixed exports retain its changes.
        string nativeModels = CharacterIdentityCatalog.Find(character, "chara_model_");
        string modelOutput = Path.Combine(output, "common", "gamedata", "character", Path.GetFileName(nativeModels));
        var characterModels = CfgTable.Read(File.ReadAllBytes(File.Exists(modelOutput) ? modelOutput : nativeModels), nativeModels);
        bool changedModels = false;
        foreach (var row in characterModels.Rows.Where(r => r.Name == "CHARA_MODEL_INFO"))
            if (replacementClothes.TryGetValue(row.UInt(5), out uint replacement))
            { row.Values[5] = unchecked((long)(int)replacement); changedModels = true; }
        if (changedModels) File.WriteAllBytes(modelOutput, characterModels.Write());
        string registry = Path.Combine(output, "cpk_list.cfg.bin");
        File.WriteAllBytes(registry, CharacterCpkRegistry.Build(File.Exists(registry) ? registry : CharacterCpkRegistry.FindSource(), output, output));
    }

    static CfgTable.Row[] Referenced(CfgTable table, CfgTable.Row model, string infoName)
    {
        var reference = table.Rows[table.Rows.IndexOf(model) + 1];
        if (reference.Name != model.Name + "_REF_INFO") throw new InvalidDataException("Missing model reference.");
        return table.Rows.Where(r => r.Name == infoName).Skip((int)reference.Int(0)).Take((int)reference.Int(1)).ToArray();
    }
    static void Append(CfgTable table, CfgTable.Row template, uint id, string name, string infoName, CfgTable.Row[] rows)
    {
        var model = template.Clone(); model.Values[0] = unchecked((long)(int)id);
        if (name != null) model.Values[1] = name;
        int original = table.Rows.FindIndex(r => r.Name == template.Name && r.UInt(0) == template.UInt(0));
        var reference = table.Rows[original + 1].Clone();
        reference.Values[0] = (long)table.Rows.Count(r => r.Name == infoName); reference.Values[1] = (long)rows.Length;
        table.Insert(infoName + "_LIST_BEG", infoName + "_LIST_END", rows);
        table.Insert(model.Name + "_LIST_BEG", model.Name + "_LIST_END", model, reference);
    }
    static void Text(CfgTable.Row row, int field, string value) { if (value == null) return; row.Types[field] = 0; row.Values[field] = value; }
    static void Put(byte[] bytes, int offset, uint value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    static string Digest(string key) => RawHeadAsset.Digest(Encoding.UTF8.GetBytes(key)).Substring(0, 20);
}
