using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace G4Composition
{
    // Native tables shared by the detail editor and batch exporter, cached per dump root.
    internal sealed partial class CharacterIdentityCatalog
    {
        internal sealed class Team
        {
            public uint Id, TextId;
            public uint[] Kits;
        }
        static CharacterIdentityCatalog cached;
        readonly string root, directory;
        readonly CfgTable bases, parameters, models;
        readonly Dictionary<string, CfgTable> texts = new();
        readonly Dictionary<uint, int> seriesTypes = new();
        readonly AvatarRecipeParser.Rdbnp uniforms;
        readonly AvatarRecipeParser.Rdbnp.List uniformList, uniformModels;
        internal readonly List<Team> Teams = new();
        internal readonly uint[] AcademicYears = new uint[4], Series = new uint[9];
        internal static readonly string[] Locales = { "de", "en", "es", "fr", "it", "ja", "pt", "zh_hans", "zh_hant" };

        internal static CharacterIdentityCatalog Get()
        {
            string root = DumpDataManager.CommonPath;
            if (cached == null || !string.Equals(cached.root, root, StringComparison.OrdinalIgnoreCase))
                cached = new CharacterIdentityCatalog(root);
            return cached;
        }
        internal static string Find(string directory, string prefix) => Directory.GetFiles(directory, prefix + "*.cfg.bin")
            .Where(p => Path.GetFileName(p).Length > prefix.Length && char.IsDigit(Path.GetFileName(p)[prefix.Length]))
            .OrderByDescending(p => p, StringComparer.Ordinal).FirstOrDefault()
            ?? throw new FileNotFoundException("找不到設定表：" + prefix, directory);
        internal static CfgTable Read(string path) => CfgTable.Read(File.ReadAllBytes(path), path);
        static AvatarRecipeParser.Rdbnp Rdb(string path) => new AvatarRecipeParser.Rdbnp(File.ReadAllBytes(path), path);

        CharacterIdentityCatalog(string common)
        {
            root = common; directory = Path.Combine(root, "gamedata", "character");
            bases = Read(Find(directory, "chara_base_")); parameters = Read(Find(directory, "chara_param_"));
            models = Read(Find(directory, "chara_model_"));
            var academic = Rdb(Path.Combine(directory, "academic_year_config.cfg.bin"));
            var years = academic.GetList("m_academicYearInfoList");
            for (int i = 0; i < years.Count; i++)
            {
                int r = years.Row(i), type = academic.Byte(r + 4);
                if (type >= 1 && type <= 3) AcademicYears[type - 1] = academic.U32(r);
            }
            var series = Rdb(Path.Combine(directory, "chara_series_config.cfg.bin"));
            var seriesList = series.GetList("m_charaSeriesInfoList");
            for (int i = 0; i < seriesList.Count; i++)
            {
                int r = seriesList.Row(i), type = series.Byte(r + 4); uint id = series.U32(r);
                seriesTypes[id] = type;
                if (type >= 1 && type <= 9) Series[type - 1] = id;
            }
            var teamData = Rdb(Find(directory, "belong_team_config_"));
            var teamList = teamData.GetList("m_belongTeamInfoList");
            Teams.Add(new Team { Id = 0, Kits = new uint[4] });
            for (int i = 0; i < teamList.Count; i++)
            {
                int r = teamList.Row(i); uint id = teamData.U32(r);
                if (id == 0 || Teams.Any(t => t.Id == id)) continue;
                Teams.Add(new Team { Id = id, TextId = teamData.U32(r + 8),
                    Kits = new[] { teamData.U32(r + 48), teamData.U32(r + 52), teamData.U32(r + 56), teamData.U32(r + 60) } });
            }
            uniforms = Rdb(Find(directory, "uniform_config_"));
            uniformList = uniforms.GetList("m_UniformInfoList"); uniformModels = uniforms.GetList("m_UniformModelInfoList");
        }
        internal CfgTable TextTable(string locale, string file)
        {
            string key = locale + "/" + file;
            if (!texts.TryGetValue(key, out var table))
            {
                string path = Path.Combine(root, "text", locale, file + ".cfg.bin");
                texts.Add(key, table = Read(path));
            }
            return table;
        }
        internal static string Noun(CfgTable table, uint id) => table.Rows.FirstOrDefault(r => r.Name == "NOUN_INFO" &&
            r.UInt(0) == id && r.Int(1) == 0)?.Text(5) ?? "";
        internal string TeamName(Team team, string locale) => team.Id == 0 ? LocalizationManager.GetText("NoTeam") :
            Noun(TextTable(locale, "team_text"), team.TextId);
        internal CharacterIdentitySettings Load(string id)
        {
            var c = bases.Rows.SingleOrDefault(r => r.Name == "CHARA_BASE_INFO" && string.Equals(r.Text(1), id, StringComparison.OrdinalIgnoreCase));
            if (c == null) throw new InvalidDataException("找不到人物資料：" + id);
            var p = parameters.Rows.FirstOrDefault(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(1) == c.UInt(0));
            var result = new CharacterIdentitySettings {
                sourceId = id, gender = (int)c.Int(11), academicYear = c.UInt(13), sourceSeries = c.UInt(15), team = c.UInt(16),
                affinity = (int)(p?.Int(2) ?? 0), position = (int)(p?.Int(3) ?? 1), subPosition = (int)(p?.Int(4) ?? 1)
            };
            result.originalGender = result.gender; result.originalAcademicYear = result.academicYear;
            result.originalSourceSeries = result.sourceSeries; result.originalTeam = result.team;
            result.originalAffinity = result.affinity; result.originalPosition = result.position; result.originalSubPosition = result.subPosition;
            foreach (string locale in Locales)
            {
                var text = TextTable(locale, "chara_text");
                string full = Noun(text, c.UInt(3)), shortName = Noun(text, c.UInt(4)), shirt = Noun(text, c.UInt(5));
                string description = TextTable(locale, "chara_description_text").Rows.FirstOrDefault(r => r.Name == "TEXT_INFO" && r.UInt(0) == c.UInt(19))?.Text(2) ?? "";
                result.locales.Add(new CharacterLocalizedIdentity { locale = locale, fullName = full, originalFullName = full,
                    shortName = shortName, originalShortName = shortName, shirtName = shirt, originalShirtName = shirt,
                    description = description, originalDescription = description, japaneseName = locale == "ja" ? full : null });
            }
            result.techniques = LoadTechniques(id);
            result.techniqueParameterId = result.techniques.FirstOrDefault()?.parameterId ?? 0;
            return result;
        }

        internal uint VoiceReference(string characterId, string avatarCode)
        {
            if (!string.IsNullOrWhiteSpace(avatarCode))
            {
                string name = AvatarRecipeParser.ResolveVoiceName(AvatarRecipeParser.Parse(avatarCode));
                var table = Rdb(Find(directory, "general_battle_voice_"));
                var voices = table.GetList("m_generalBattleVoice");
                for (int i = 0; i < voices.Count; i++)
                {
                    int row = voices.Row(i);
                    if (table.String(row + voices.Field("voiceIdString")) == name)
                        return table.U32(row + voices.Field("voiceId"));
                }
                throw new InvalidDataException("找不到 Avatar 的遊戲語音 ID：" + name);
            }
            var character = bases.Rows.FirstOrDefault(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == characterId)
                ?? throw new InvalidDataException("找不到語音來源人物：" + characterId);
            var visited = new HashSet<uint>();
            while (true)
            {
                if (!visited.Add(character.UInt(0))) throw new InvalidDataException("語音來源人物參照循環。");
                uint reference = character.UInt(9);
                if (reference == 0) return character.UInt(0);
                var next = bases.Rows.FirstOrDefault(r => r.Name == "CHARA_BASE_INFO" && r.UInt(0) == reference);
                if (next == null)
                {
                    if (AvatarRecipeParser.ResolveGenericVoiceName(root, reference) == null)
                        throw new InvalidDataException("找不到語音來源設定：" + characterId);
                    return reference;
                }
                character = next;
            }
        }

        internal uint FacialReference(CharacterMakerPart head)
        {
            // Generated Avatar heads use the same native 4x2 facial contract as c01000010.
            string id = string.IsNullOrWhiteSpace(head.avatarCode) ? head.sourceId : "c01000010";
            var character = bases.Rows.FirstOrDefault(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == id)
                ?? throw new InvalidDataException("找不到頭部的表情來源：" + id);
            var model = models.Rows.Single(r => r.Name == "CHARA_MODEL_INFO" && r.UInt(0) == character.UInt(6));
            return model.UInt(36);
        }

        // Generic IE/GO unaffiliated teams reference uniform_config nameId 2961452819.
        // Their character model rows often carry Raimon as a fallback, not their actual kit.
        internal uint[] Equipment(uint teamId, uint seriesId, int gender, bool keeper)
        {
            // Uniform type selects the team kit, independently of character gender.
            int type = 0, selected = -1;
            if (teamId == 0)
            {
                const uint unaffiliatedKit = 2961452819;
                for (int i = 0; i < uniformList.Count; i++)
                {
                    int row = uniformList.Row(i);
                    if (uniforms.U32(row) != unaffiliatedKit) continue;
                    uint tuple = uniforms.U32(row + 4);
                    int start = (int)(tuple & 0xffff), count = (int)(tuple >> 16);
                    for (int j = 0; j < count; j++)
                    {
                        int candidate = uniformModels.Row(start + j);
                        if (selected < 0) selected = candidate;
                        if (uniforms.Byte(candidate + 92) == type) { selected = candidate; break; }
                    }
                    break;
                }
            }
            else
            {
                var team = Teams.SingleOrDefault(t => t.Id == teamId) ?? throw new InvalidDataException("無效的隊伍設定。");
                seriesTypes.TryGetValue(seriesId, out int source);
                int preferred = source <= 3 ? 0 : source <= 6 ? 1 : source <= 8 ? 2 : 3;
                foreach (uint kit in new[] { team.Kits[preferred] }.Concat(team.Kits).Where(k => k != 0).Distinct())
                {
                    for (int i = 0; i < uniformList.Count; i++)
                    {
                        int row = uniformList.Row(i); if (uniforms.U32(row) != kit) continue;
                        uint tuple = uniforms.U32(row + 4); int start = (int)(tuple & 0xffff), count = (int)(tuple >> 16);
                        for (int j = 0; j < count; j++)
                        {
                            int candidate = uniformModels.Row(start + j);
                            if (selected < 0) selected = candidate;
                            if (uniforms.Byte(candidate + 92) == type) { selected = candidate; break; }
                        }
                        break;
                    }
                    if (selected >= 0) break;
                }
            }
            if (selected < 0) throw new InvalidDataException("找不到隊伍的預設裝備。");
            uint clothing = uniforms.U32(selected + (keeper ? 4 : 0)), shoes = uniforms.U32(selected + (keeper ? 76 : 72));
            uint gloves = keeper ? uniforms.U32(selected + 88) : 0;
            if (clothing == 0 || shoes == 0 || (keeper && gloves == 0)) throw new InvalidDataException("隊伍裝備資料不完整。");
            return new[] { clothing, shoes, gloves };
        }
    }
}
