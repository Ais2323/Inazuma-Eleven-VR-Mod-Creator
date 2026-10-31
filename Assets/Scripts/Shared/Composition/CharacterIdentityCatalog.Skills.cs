using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace G4Composition
{
    internal sealed partial class CharacterIdentityCatalog
    {
        internal sealed class SkillOption { internal uint Id; internal string Label, Resource; internal int Category = -1, Affinity; internal bool Aura; }
        readonly Dictionary<uint, SkillOption> skillMetadata = new();
        readonly Dictionary<string, string> miximaxPrefixes = new();
        internal CfgTable AuraTable { get; private set; }
        internal string TransformationTitle(int category, string locale, string characterName)
        {
            if (category != 5) return characterName ?? "";
            if (!miximaxPrefixes.TryGetValue(locale, out string prefix)) {
                LoadSkillIds();
                string path = Path.Combine(root, "text", locale, "skill_text.cfg.bin");
                if (!File.Exists(path)) path = Path.Combine(root, "text", "en", "skill_text.cfg.bin");
                var text = Read(path);
                string Label(string resource) => Noun(text, AuraTable.Rows.Single(r => r.Name == "AURA_CMD_INFO" && r.Text(1) == resource).UInt(2));
                string first = Label("wmm00010"), second = Label("wmm00020");
                int length = 0;
                while (length < Math.Min(first.Length, second.Length) && first[length] == second[length]) length++;
                prefix = first.Substring(0, length);
                if (string.IsNullOrWhiteSpace(prefix) || !prefix.Any(c => c == ':' || c == '・' || c == '·'))
                    throw new InvalidDataException("Cannot determine native Miximax title format: " + locale);
                if (locale == "zh_hant" || locale == "zh_hans") prefix = prefix.Replace('·', '．');
                miximaxPrefixes[locale] = prefix;
            }
            return prefix + characterName;
        }
        Dictionary<uint, uint> skillNames;
        readonly Dictionary<string, List<SkillOption>> skillOptions = new();
        internal IReadOnlyDictionary<uint, uint> SkillIds { get { LoadSkillIds(); return skillNames; } }
        void LoadSkillIds()
        {
            if (skillNames != null) return;
            string directory = Path.Combine(root, "gamedata", "skill");
            var choices = Directory.GetFiles(directory, "skill_config*.cfg.bin")
                .Select(p => SkillInventory.Read(File.ReadAllBytes(p), p)).OrderByDescending(s => s.Count).ToArray();
            if (choices.Length == 0) throw new InvalidDataException("No skill configuration found.");
            var found = choices[0];
            string regularPath = Directory.GetFiles(directory, "skill_config*.cfg.bin").OrderByDescending(p => new FileInfo(p).Length).First();
            var regular = Rdb(regularPath); var list = regular.GetList("m_skillInfoList");
            for (int i = 0; i < list.Count; i++) {
                int row = list.Row(i); uint id = regular.U32(row + list.Field("skillID"));
                skillMetadata[id] = new SkillOption { Id = id, Category = regular.Byte(row + list.Field("category")) - 1,
                    Affinity = regular.Byte(row + list.Field("element")), Resource = regular.String(row + list.Field("skillIDStr")) };
            }
            var aura = Directory.GetFiles(directory, "aura_skill_config*.cfg.bin")
                .Select(Read).OrderByDescending(t => t.Rows.Count(r => r.Name == "AURA_CMD_INFO")).FirstOrDefault();
            AuraTable = aura;
            if (aura != null) foreach (var row in aura.Rows.Where(r => r.Name == "AURA_CMD_INFO")) {
                found[row.UInt(0)] = row.UInt(2);
                int category = checked((int)row.Int(10));
                // UI order: keshin, miximax, soul, awakening power, mode, kizuna.
                int ui = category == 0 || category == 1 ? 4 : category == 2 ? 5 : category == 3 ? 6 : category == 5 ? 7 : category == 6 ? 8 : category == 4 ? 9 : -1;
                skillMetadata[row.UInt(0)] = new SkillOption { Id = row.UInt(0), Resource = row.Text(1),
                    Category = ui, Affinity = checked((int)row.Int(8)), Aura = true };
            }
            skillNames = found;
        }
        internal List<SkillOption> SkillOptions(string locale)
        {
            if (skillOptions.TryGetValue(locale, out var cached)) return cached;
            LoadSkillIds(); var names = new Dictionary<uint, string>();
            foreach (string language in new[] { "ja", "en", locale }.Distinct())
            {
                string path = Path.Combine(root, "text", language, "skill_text.cfg.bin");
                if (!File.Exists(path)) continue;
                foreach (var row in Read(path).Rows.Where(r => r.Name == "NOUN_INFO" && r.Values.Length >= 6 && r.Int(1) == 0))
                    if (!string.IsNullOrWhiteSpace(row.Text(5))) names[row.UInt(0)] = System.Text.RegularExpressions.Regex.Replace(row.Text(5), @"\[([^\[\]/]+)/[^\[\]]+\]", "$1").Replace("\\n", " ").Replace("\r", " ").Replace("\n", " ").Trim();
            }
            cached = skillNames.Where(p => p.Key != 0).Select(p => new SkillOption {
                Id = p.Key, Label = (names.TryGetValue(p.Value, out string name) ? name : "0x" + p.Value.ToString("X8")) + " · " + p.Key.ToString("X8"),
                Category = skillMetadata[p.Key].Category, Affinity = skillMetadata[p.Key].Affinity,
                Resource = skillMetadata[p.Key].Resource, Aura = skillMetadata[p.Key].Aura
            }).OrderBy(p => p.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            cached.Insert(0, new SkillOption { Id = 0, Label = LocalizationManager.GetText("SkillNone") });
            skillOptions.Add(locale, cached); return cached;
        }
        internal List<CharacterTechniqueSettings> LoadTechniques(string id)
        {
            var character = bases.Rows.Single(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == id);
            return parameters.Rows.Where(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(1) == character.UInt(0)).OrderBy(r => r.Int(41) == 0 ? 0 : 1).Select(r => {
                var skills = Enumerable.Range(0, 9).Select(i => r.UInt(11 + i * 2)).ToArray();
                var levels = Enumerable.Range(0, 9).Select(i => checked((int)r.Int(12 + i * 2))).ToArray();
                return new CharacterTechniqueSettings { parameterId = r.UInt(0), rarity = checked((int)r.Int(41)), affinity = checked((int)r.Int(2)),
                    hasBuildType = true, buildType = checked((int)r.Int(5)), originalBuildType = checked((int)r.Int(5)),
                    skills = skills, originalSkills = (uint[])skills.Clone(), levels = levels, originalLevels = (int[])levels.Clone() };
            }).ToList();
        }
    }
}
