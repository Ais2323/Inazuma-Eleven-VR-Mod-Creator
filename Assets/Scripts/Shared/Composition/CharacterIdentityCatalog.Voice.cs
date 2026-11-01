using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using G4Runtime;

namespace G4Composition
{
    internal sealed partial class CharacterIdentityCatalog
    {
        internal sealed class NativeVoiceEntry
        {
            internal CharacterVoiceOverride Definition;
            internal string[] CueNames;
        }

        internal List<NativeVoiceEntry> NativeVoiceEntries(string bank, string locale)
        {
            var names = new VoiceCueBank(System.IO.Path.ChangeExtension(bank, ".acb")).AvailableCueNames();
            var available = names.Select(name => new { Name = name, Suffix = name.Substring(name.IndexOf('_') + 1) }).ToArray();
            var options = SkillOptions(locale);
            var result = new List<NativeVoiceEntry>();
            foreach (var group in available.Select(n => Regex.Match(n.Suffix, @"^(?:a\d{6}_)?(wh[sodk]\d{5}|[ka]\d{6}_inc)(?:_sp_\d+|_\d+)?$"))
                .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct().OrderBy(s => s, StringComparer.Ordinal))
            {
                string category = "cue:" + group;
                var option = options.FirstOrDefault(o => VoiceCueResource(o) == group);
                int page = group.EndsWith("_inc", StringComparison.Ordinal) ? 5 :
                    group[2] == 's' ? 1 : group[2] == 'o' ? 2 : group[2] == 'd' ? 3 : 4;
                result.Add(new NativeVoiceEntry {
                    Definition = new CharacterVoiceOverride { category = category, page = page, skillId = option?.Id ?? 0, skillResource = option?.Resource ?? group },
                    CueNames = available.Where(n => NormalVoiceCategories.Matches(category, n.Suffix)).Select(n => n.Name).Distinct().ToArray()
                });
            }
            return result;
        }

        Dictionary<uint, string> summonVoiceModels;

        internal string VoiceCueResource(SkillOption option)
        {
            LoadSkillIds();
            if (option == null || option.Id == 0) return null;
            if (option.Category >= 0 && option.Category <= 3)
                return Regex.IsMatch(option.Resource ?? "", @"^wh[sodk]\d{5}$") ? option.Resource : null;
            if (option.Category != 4 && option.Category != 6) return null;
            var aura = AuraTable.Rows.FirstOrDefault(r => r.Name == "AURA_CMD_INFO" && r.UInt(0) == option.Id);
            if (aura == null || aura.Int(10) != 0 && aura.Int(10) != 3 || aura.UInt(7) == 0) return null;
            if (summonVoiceModels == null)
            {
                summonVoiceModels = new Dictionary<uint, string>();
                // Resolve the model referenced by the aura, not the technique number:
                // e.g. wks00720 uses k000740; souls use a###### rather than k######.
                foreach (string prefix in new[] { "k", "a" })
                    for (int i = 0; i < 10000; i++)
                    {
                        string model = prefix + i.ToString("D6");
                        summonVoiceModels[G4TextureParser.Crc32(model)] = model;
                    }
            }
            return summonVoiceModels.TryGetValue(aura.UInt(7), out string stem) ? stem + "_inc" : null;
        }
    }
}
