using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;

internal static class CharacterSkillTransformationExporter
{
    // Skill slots resolve through the item catalog before the aura command catalog.
    // An aura-only ID looks like an empty equip slot in the ability board.
    internal static void RegisterItem(CfgTable table, uint templateId, uint id, string commandName)
    {
        const string listName = "ITEM_SPECIAL_SKILL_INFO";
        int begin = table.Rows.FindIndex(r => r.Name == listName + "_LIST_BEG");
        int end = table.Rows.FindIndex(begin + 1, r => r.Name.EndsWith("_LIST_BEG", StringComparison.Ordinal));
        if (begin < 0 || end < 0) throw new InvalidDataException(table.Source + ": missing skill-item list boundaries.");
        var entries = table.Rows.Skip(begin + 1).Take(end - begin - 1).Where(r => r.Name == listName).ToList();
        var native = entries.SingleOrDefault(r => r.UInt(0) == templateId)
            ?? throw new InvalidDataException(table.Source + ": missing native transformation item.");
        var existing = entries.SingleOrDefault(r => r.UInt(0) == id);
        if (existing != null && (existing.Text(11) != commandName || existing.UInt(16) != id))
            throw new InvalidDataException("Transformation item ID collision: " + commandName);
        var item = native.Clone();
        item.Values[0] = item.Values[16] = unchecked((long)(int)id); item.Values[11] = commandName;
        // Native item serials are shared across item categories, unlike list-local sort indices.
        item.Values[4] = existing?.Values[4] ?? checked(table.Rows.Where(r => r.Name.StartsWith("ITEM_", StringComparison.Ordinal) &&
            r.Values.Length > 16 && r.Types[4] == 1 && r.Values[11] is string).Max(r => r.Int(4)) + 1);
        if (existing != null) table.Rows[table.Rows.IndexOf(existing)] = item;
        else { table.Rows.Insert(end++, item); entries.Add(item); }
        var sortTemplate = table.Rows.Skip(begin + 1).Take(end - begin - 1).First(r => r.Name == "__SORT_INDEX").Clone();
        for (int at = end - 1; at > begin; at--) if (table.Rows[at].Name == "__SORT_INDEX") table.Rows.RemoveAt(at);
        end = table.Rows.FindIndex(begin + 1, r => r.Name.EndsWith("_LIST_BEG", StringComparison.Ordinal));
        // Re-read physical order: replacing a row must retain its logical lookup index.
        entries = table.Rows.Skip(begin + 1).Take(end - begin - 1).Where(r => r.Name == listName).ToList();
        table.Rows[begin].Values[0] = (long)entries.Count;
        table.Rows.InsertRange(end, entries.Select((r,i) => new { Id = r.UInt(0), Index = i }).OrderBy(x => x.Id).Select(x => {
            var row = sortTemplate.Clone(); row.Values[0] = (long)x.Index; return row;
        }));
    }
    internal static void ValidateReferences(CharacterMakerDocument[] documents)
    {
        foreach (var document in documents)
            foreach (var variant in document.identity?.techniques ?? new List<CharacterTechniqueSettings>())
                foreach (var item in variant.transformations ?? new List<CharacterSkillTransformation>()) {
                    if (item == null || variant.skills?.Length != 9 || item.slot < 0 || item.slot >= 9 || item.category != 5 && item.category != 8 || variant.skills[item.slot] != item.auraId)
                        throw new InvalidDataException("Invalid transformation skill slot.");
                    if (item.useExistingModel) {
                        if (item.category != 5) throw new InvalidDataException("Only Miximax can bind a skill to an existing model.");
                    } else Reference(item, documents);
                }
    }
    static int Reference(CharacterSkillTransformation item, CharacterMakerDocument[] documents)
    {
        var matches = documents.Select((d, i) => new { d, i }).Where(x => !string.IsNullOrWhiteSpace(item.characterKey)
            ? x.d.IsNew && x.d.newCharacterKey == item.characterKey
            : !x.d.IsNew && string.Equals(x.d.targetId, item.characterId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException(LocalizationManager.GetText("SkillMissingExportCharacter") + ": " + item.characterName);
        return matches[0].i;
    }

    // Resolve references only after every character has its final namespace-dependent IDs.
    internal static void Write(CharacterMakerDocument[] originals, IList<CharacterMakerDocument> resolved, CharacterCatalog catalog, string stage, string destination, string modNamespace = null)
    {
        var source = CharacterIdentityCatalog.Get();
        source.SkillOptions("en");
        CfgTable aura = null, changes = null;
        var itemTables = new Dictionary<string, CfgTable>(StringComparer.OrdinalIgnoreCase);
        var sharedCommands = new Dictionary<string, CfgTable.Row>(StringComparer.Ordinal);
        var sharedAliases = new Dictionary<uint, HashSet<uint>>();
        CfgTable Load(string path) {
            string exported = Path.Combine(destination, "common", path.Substring(catalog.Common.Length + 1));
            return CfgTable.Read(File.ReadAllBytes(File.Exists(exported) ? exported : path), path);
        }
        CfgTable Aura() => aura ??= Load(source.AuraTable.Source);
        CfgTable Changes() => changes ??= Load(CharacterIdentityCatalog.Find(Path.Combine(catalog.Common, "gamedata", "character"), "chara_change_"));
        void Set(CfgTable.Row row, int field, uint value) => row.Values[field] = unchecked((long)(int)value);
        var nameOwners = new Dictionary<uint, string>();
        var nativeNameIds = new Dictionary<string, HashSet<uint>>(StringComparer.OrdinalIgnoreCase);
        (uint Name, uint Description) WriteText(string commandName, CharacterMakerDocument target, CharacterSkillTransformation custom) {
            uint textId = G4TextureParser.Crc32(commandName + "_name");
            uint descriptionId = G4TextureParser.Crc32(commandName + "_description");
            foreach (uint id in new[] { textId, descriptionId }) {
                if (nameOwners.TryGetValue(id, out string owner) && owner != commandName)
                    throw new InvalidDataException("Transformation text ID collision: " + commandName);
                nameOwners[id] = commandName;
            }
            foreach (string language in Directory.GetDirectories(Path.Combine(catalog.Common, "text"))) {
                string path = Path.Combine(language, "skill_text.cfg.bin");
                if (!File.Exists(path)) continue;
                if (!nativeNameIds.TryGetValue(path, out var nativeIds)) {
                    nativeNameIds[path] = nativeIds = new HashSet<uint>(CfgTable.Read(File.ReadAllBytes(path), path).Rows
                        .Where(r => r.Name == "NOUN_INFO" || r.Name == "TEXT_INFO").Select(r => r.UInt(0)));
                }
                if (nativeIds.Contains(textId) || nativeIds.Contains(descriptionId))
                    throw new InvalidDataException("Transformation text ID collides with native text: " + commandName);
                if (!catalog.TextTables.TryGetValue(path, out var table)) {
                    catalog.TextTables[path] = table = Load(path);
                }
                string languageCode = Path.GetFileName(language);
                string label = target.identity?.locales?.FirstOrDefault(x => x.locale == languageCode && !string.IsNullOrWhiteSpace(x.fullName))?.fullName
                    ?? target.identity?.locales?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.fullName))?.fullName
                    ?? custom.characterName;
                if (custom.category == 8) label = custom.ModeNameForLocale(languageCode, label);
                var noun = table.Rows.SingleOrDefault(r => r.Name == "NOUN_INFO" && r.UInt(0) == textId && r.Int(1) == 0);
                if (noun == null) {
                    noun = table.Rows.First(r => r.Name == "NOUN_INFO" && r.Int(1) == 0).Clone();
                    Set(noun, 0, textId); table.Insert("NOUN_INFO_BEGIN", "NOUN_INFO_END", noun);
                }
                noun.Values[5] = source.TransformationTitle(custom.category, languageCode, label);
                // The third page is the chosen character's description, in the same locale.
                // Keep the character text's native newline encoding unchanged.
                string description = target.identity?.locales?.FirstOrDefault(x => x.locale == languageCode)?.description;
                if (description == null) {
                    var targetBase = catalog.Base.Rows.Single(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == target.targetId);
                    string descriptionPath = Path.Combine(language, "chara_description_text.cfg.bin");
                    if (catalog.TextTables.TryGetValue(descriptionPath, out var characterText) || File.Exists(descriptionPath)) {
                        characterText ??= Load(descriptionPath);
                        description = characterText.Rows.FirstOrDefault(r => r.Name == "TEXT_INFO" && r.UInt(0) == targetBase.UInt(19))?.Text(2);
                    }
                }
                var text = table.Rows.SingleOrDefault(r => r.Name == "TEXT_INFO" && r.UInt(0) == descriptionId);
                if (text == null) {
                    text = table.Rows.First(r => r.Name == "TEXT_INFO").Clone();
                    Set(text, 0, descriptionId); table.Insert("TEXT_INFO_BEGIN", "TEXT_INFO_END", text);
                }
                text.Values[2] = description ?? "";
            }
            return (textId, descriptionId);
        }
        CfgTable.Row[] References(CfgTable table, CfgTable.Row command) => table.Rows.Skip(table.Rows.IndexOf(command) + 1)
            .TakeWhile(r => r.Name.StartsWith("AURA_CMD_INFO_REF_", StringComparison.Ordinal)).ToArray();
        bool SameDefinition(CfgTable.Row row, CfgTable.Row native, uint donor, uint parameter, uint boundSkill, uint modeTarget, Dictionary<string, string> modeTitles) {
            if (!row.Text(1).StartsWith("cm_aura_", StringComparison.Ordinal) || row.UInt(7) != donor || row.UInt(13) != parameter ||
                native.Int(10) == 2 && row.UInt(6) != boundSkill) return false;
            if (native.Int(10) == 6) {
                foreach (var title in modeTitles) {
                    string path = Path.Combine(catalog.Common, "text", title.Key, "skill_text.cfg.bin");
                    if (!catalog.TextTables.TryGetValue(path, out var text)) catalog.TextTables[path] = text = Load(path);
                    if (CharacterIdentityCatalog.Noun(text, row.UInt(2)) != title.Value) return false;
                }
                var owners = catalog.Parameters.Rows.Where(r => r.Name == "CHARA_PARAM_INFO" &&
                    Enumerable.Range(0, 9).Any(i => r.UInt(11 + i * 2) == row.UInt(0))).Select(r => r.UInt(0)).ToArray();
                if (owners.Length == 0 || owners.Any(id => !Changes().Rows.Any(r => r.Name == "CHARA_MODE_CHANGE" && r.UInt(0) == id && r.UInt(1) == modeTarget))) return false;
            }
            for (int i = 4; i < native.Values.Length; i++)
                if (i != 6 && i != 7 && i != 13 && !Equals(row.Values[i], native.Values[i])) return false;
            var expected = References(source.AuraTable, native);
            var actual = References(Aura(), row);
            return expected.Where(r => r.Name != "AURA_CMD_INFO_REF_CHARA").All(r =>
                actual.Any(a => a.Name == r.Name && a.Values.SequenceEqual(r.Values)));
        }
        foreach (var owner in resolved) {
            foreach (var variant in owner.identity?.techniques ?? new List<CharacterTechniqueSettings>()) {
                uint? selectedModeTarget = null;
                var param = catalog.Parameters.Rows.Single(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(0) == variant.parameterId);
                var transforms = variant.transformations ?? new List<CharacterSkillTransformation>();
                for (int slot = 0; slot < variant.skills.Length; slot++) {
                    uint skill = variant.skills[slot];
                    var native = source.AuraTable.Rows.SingleOrDefault(r => r.Name == "AURA_CMD_INFO" && r.UInt(0) == skill);
                    var custom = transforms.SingleOrDefault(t => t.slot == slot);
                    if (native == null) continue;
                    int type = checked((int)native.Int(10));
                    if (custom == null && type == 2) {
                        Aura();
                        if (!sharedAliases.ContainsKey(skill)) sharedAliases[skill] = new HashSet<uint> { skill };
                    }
                    uint modeTarget = 0;
                    if (custom != null) {
                        var target = custom.useExistingModel ? null : resolved[Reference(custom, originals)];
                        var targetBase = target == null ? null : catalog.Base.Rows.Single(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == target.targetId);
                        modeTarget = target?.identity.techniqueParameterId ?? 0;
                        var table = Aura();
                        uint donor = custom.category == 5 ? targetBase?.UInt(0) ?? native.UInt(7) : native.UInt(7);
                        uint transformed = custom.category == 5 ? target?.identity.techniqueParameterId ?? native.UInt(13) : native.UInt(13);
                        string definition = native.UInt(0).ToString("X8") + ":" + donor.ToString("X8") + ":" + transformed.ToString("X8") + ":" + custom.bindSkill.ToString("X8") + ":" + (type == 6 ? modeTarget : 0).ToString("X8");
                        Dictionary<string, string> modeTitles = null;
                        if (custom.category == 8) {
                            modeTitles = Directory.GetDirectories(Path.Combine(catalog.Common, "text"))
                                .Where(p => File.Exists(Path.Combine(p, "skill_text.cfg.bin"))).Select(Path.GetFileName)
                                .OrderBy(p => p, StringComparer.Ordinal).ToDictionary(language => language, language => custom.ModeNameForLocale(language,
                                    target?.identity?.locales?.FirstOrDefault(x => x.locale == language && !string.IsNullOrWhiteSpace(x.fullName))?.fullName
                                    ?? target?.identity?.locales?.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.fullName))?.fullName ?? custom.characterName) ?? "");
                            foreach (var title in modeTitles) definition += ":name:" + title.Key.Length + ":" + title.Key + ":" + title.Value.Length + ":" + title.Value;
                        }
                        if (sharedCommands.TryGetValue(definition, out var shared)) {
                            Set(param, 11 + slot * 2, shared.UInt(0));
                            if (type != 6) continue;
                        }
                        if (shared == null) {
                            // Use definition-derived IDs for fresh exports. Reuse an existing matching
                            // command when updating, so previously acquired skill IDs stay valid.
                            var matches = table.Rows.Where(r => r.Name == "AURA_CMD_INFO" && SameDefinition(r, native, donor, transformed, custom.bindSkill, modeTarget, modeTitles))
                                .OrderBy(r => r.Text(1), StringComparer.Ordinal).ToArray();
                            string key = CharacterMakerExportBatch.NamespacedKey(modNamespace, "transformation:" + definition);
                            string name = matches.FirstOrDefault()?.Text(1) ?? "cm_aura_shared_" + G4TextureParser.Crc32Utf8(key).ToString("X8");
                            uint id = G4TextureParser.Crc32(name);
                            var existing = table.Rows.SingleOrDefault(r => r.Name == "AURA_CMD_INFO" && r.UInt(0) == id);
                            if (existing == null && catalog.Skills.ContainsKey(id))
                                throw new InvalidDataException("Transformation skill ID collides with a native skill: " + name);
                            if (existing != null && (existing.Text(1) != name || !SameDefinition(existing, native, donor, transformed, custom.bindSkill, modeTarget, modeTitles)))
                                throw new InvalidDataException("Transformation skill ID collision: " + name);
                            var clone = native.Clone(); Set(clone, 0, id); clone.Values[1] = name;
                            foreach (string itemPath in Directory.GetFiles(Path.Combine(catalog.Common, "gamedata", "item"), "item_config_*.cfg.bin")) {
                                if (!itemTables.TryGetValue(itemPath, out var items)) itemTables[itemPath] = items = Load(itemPath);
                                RegisterItem(items, native.UInt(0), id, name);
                            }
                            // A native command is only a structural template. Its Tyrannosaurus/etc.
                            // display text must not become the custom character's transformation name.
                            if (target != null) {
                                var textIds = WriteText(name, target, custom);
                                Set(clone, 2, textIds.Name); Set(clone, 3, textIds.Description);
                            }
                            if (custom.category == 5) {
                                if (!source.SkillOptions("en").Any(c => c.Id == custom.bindSkill && c.Category >= 0 && c.Category <= 3))
                                    throw new InvalidDataException("Invalid Miximax bound skill.");
                                Set(clone, 6, custom.bindSkill);
                                if (targetBase != null) {
                                    Set(clone, 7, targetBase.UInt(0));
                                    // Native Miximax stores the donor/base portrait in [7], but the
                                    // actual transformed model (and front portrait) is parameter [13].
                                    Set(clone, 13, target.identity.techniqueParameterId);
                                }
                            } else Set(clone, 6, id);
                            if (existing != null) table.Rows[table.Rows.IndexOf(existing)] = clone;
                            else {
                                var companions = new List<CfgTable.Row> { clone };
                                int at = source.AuraTable.Rows.IndexOf(native) + 1;
                                while (at < source.AuraTable.Rows.Count && source.AuraTable.Rows[at].Name.StartsWith("AURA_CMD_INFO_REF_", StringComparison.Ordinal))
                                    companions.Add(source.AuraTable.Rows[at++].Clone());
                                table.Insert("AURA_CMD_INFO_LIST_BEG", "AURA_CMD_INFO_LIST_END", companions.ToArray());
                            }
                            sharedCommands[definition] = clone;
                            if (!sharedAliases.TryGetValue(id, out var aliases)) sharedAliases[id] = aliases = new HashSet<uint>();
                            aliases.Add(id);
                            foreach (var match in matches) {
                                aliases.Add(match.UInt(0));
                                match.Values[2] = clone.Values[2]; match.Values[3] = clone.Values[3];
                            }
                            Set(param, 11 + slot * 2, id);
                        }
                    }
                    if (type != 6) continue;
                    if (custom == null) {
                        // Native mode aura commands name the source character; the actual target is a parameter mapping.
                        string name = native.Text(1).Substring("mode_change_".Length).Split('_')[0];
                        var character = catalog.Base.Rows.SingleOrDefault(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == name)
                            ?? throw new InvalidDataException("Missing native mode-change source: " + name);
                        var parameters = new HashSet<uint>(catalog.Parameters.Rows.Where(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(1) == character.UInt(0)).Select(r => r.UInt(0)));
                        var mapping = Changes().Rows.FirstOrDefault(r => r.Name == "CHARA_MODE_CHANGE" && parameters.Contains(r.UInt(0)))
                            ?? throw new InvalidDataException("Missing native mode-change mapping: " + name);
                        modeTarget = mapping.UInt(1);
                    }
                    var mode = Changes();
                    if (selectedModeTarget.HasValue && selectedModeTarget.Value != modeTarget)
                        throw new InvalidDataException("A technique variant cannot target multiple different mode-change characters.");
                    selectedModeTarget = modeTarget;
                    var old = mode.Rows.SingleOrDefault(r => r.Name == "CHARA_MODE_CHANGE" && r.UInt(0) == variant.parameterId);
                    if (old != null) Set(old, 1, modeTarget);
                    else {
                        var row = mode.Rows.First(r => r.Name == "CHARA_MODE_CHANGE").Clone();
                        Set(row, 0, variant.parameterId); Set(row, 1, modeTarget);
                        int begin = mode.Rows.FindIndex(r => r.Name == "CHARA_MODE_CHANGE_LIST_BEG");
                        int count = checked((int)mode.Rows[begin].Int(0));
                        mode.Rows.Insert(begin + 1 + count, row); mode.Rows[begin].Values[0] = (long)(count + 1);
                    }
                }
            }
        }
        if (aura != null) {
            // Fourth page: derive users from actual parameter slots, not the native
            // template's Fei/etc. list. Deduplicate variants belonging to one character.
            foreach (var group in sharedAliases) {
                uint[] users = catalog.Parameters.Rows.Where(r => r.Name == "CHARA_PARAM_INFO" &&
                    Enumerable.Range(0, 9).Any(i => group.Value.Contains(r.UInt(11 + i * 2))))
                    .Select(r => r.UInt(1)).Distinct().OrderBy(id => id).ToArray();
                if (users.Length == 0) throw new InvalidDataException("Transformation has no skill users.");
                foreach (uint alias in group.Value) {
                    var command = aura.Rows.Single(r => r.Name == "AURA_CMD_INFO" && r.UInt(0) == alias);
                    var reference = References(aura, command).Single(r => r.Name == "AURA_CMD_INFO_REF_CHARA");
                    var rows = aura.Rows.Where(r => r.Name == "AURA_CMD_CHARA").ToArray();
                    int start = checked((int)reference.Int(0)), count = checked((int)reference.Int(1));
                    if (start >= 0 && count == users.Length && start <= rows.Length - count && rows.Skip(start).Take(count).Select(r => r.UInt(0)).SequenceEqual(users)) continue;
                    Set(reference, 0, (uint)rows.Length); Set(reference, 1, (uint)users.Length);
                    aura.Insert("AURA_CMD_CHARA_LIST_BEG", "AURA_CMD_CHARA_LIST_END", users.Select(id => {
                        var row = rows[0].Clone(); Set(row, 0, id); return row;
                    }).ToArray());
                }
            }
            // The native lookup holds logical row indices sorted by unsigned command ID.
            var commands = aura.Rows.Where(r => r.Name == "AURA_CMD_INFO").ToArray();
            var template = aura.Rows.First(r => r.Name == "__SORT_INDEX").Clone();
            aura.Rows.RemoveAll(r => r.Name == "__SORT_INDEX");
            int end = aura.Rows.FindIndex(r => r.Name == "AURA_CMD_INFO_LIST_END");
            var sorted = commands.Select((r,i) => new { Id = r.UInt(0), Index = i }).OrderBy(x => x.Id).Select(x => {
                var row = template.Clone(); Set(row, 0, (uint)x.Index); return row;
            });
            aura.Rows.InsertRange(end, sorted);
        }
        foreach (var table in new[] { aura, changes }.Where(t => t != null).Concat(itemTables.Values)) {
            string path = Path.Combine(stage, "common", table.Source.Substring(catalog.Common.Length + 1));
            Directory.CreateDirectory(Path.GetDirectoryName(path)); File.WriteAllBytes(path, table.Write());
        }
    }
}
