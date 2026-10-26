using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
namespace G4Composition
{
    internal sealed partial class CharacterIdentityCatalog
    {
        internal sealed class SkillOption { internal uint Id; internal string Label; }
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
            var aura = Directory.GetFiles(directory, "aura_skill_config*.cfg.bin")
                .Select(p => Read(p).Rows.Where(r => r.Name == "AURA_CMD_INFO" && r.Values.Length > 2).ToArray())
                .OrderByDescending(rows => rows.Length).FirstOrDefault();
            if (aura != null) foreach (var row in aura) found[row.UInt(0)] = row.UInt(2);
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
                Id = p.Key, Label = (names.TryGetValue(p.Value, out string name) ? name : "0x" + p.Value.ToString("X8")) + " · " + p.Key.ToString("X8")
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
