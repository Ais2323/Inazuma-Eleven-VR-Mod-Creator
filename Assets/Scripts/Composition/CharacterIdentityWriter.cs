using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace G4Composition
{
    // One writer per batch: edits to several characters/locales accumulate before writing.
    internal sealed class CharacterIdentityWriter
    {
        readonly CfgTable bases;
        readonly Dictionary<string, CfgTable> tables = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> dirty = new(StringComparer.OrdinalIgnoreCase);
        internal CharacterIdentityWriter(CfgTable bases) { this.bases = bases; }
        CfgTable Table(string path)
        {
            if (!tables.TryGetValue(path, out var table)) tables[path] = table = CharacterIdentityCatalog.Read(path);
            return table;
        }
        static void Set(CfgTable.Row row, int index, long value) => row.Values[index] = unchecked((long)(int)value);
        internal bool Apply(CharacterIdentitySettings identity, string targetId, CfgTable.Row character, CfgTable.Row model)
        {
            if (identity == null) return false; // Older saves preserve the native identity.
            if (!string.Equals(identity.sourceId, targetId, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("人物資訊與匯出目標不一致。");
            var catalog = CharacterIdentityCatalog.Get();
            if ((identity.gender != identity.originalGender && !new[] { 1, 2, 5 }.Contains(identity.gender)) ||
                (identity.affinity != identity.originalAffinity && (identity.affinity < 0 || identity.affinity > 4)) ||
                (identity.position != identity.originalPosition && (identity.position < 1 || identity.position > 4)) ||
                (identity.subPosition != identity.originalSubPosition && (identity.subPosition < 1 || identity.subPosition > 4)) ||
                (identity.academicYear != identity.originalAcademicYear && !catalog.AcademicYears.Contains(identity.academicYear)) ||
                (identity.sourceSeries != identity.originalSourceSeries && !catalog.Series.Contains(identity.sourceSeries)))
                throw new InvalidDataException("人物屬性超出範圍。");
            UpdateBase(character, 11, identity.gender, identity.originalGender);
            UpdateBase(character, 13, identity.academicYear, identity.originalAcademicYear);
            UpdateBase(character, 15, identity.sourceSeries, identity.originalSourceSeries);
            UpdateBase(character, 16, identity.team, identity.originalTeam);
            if (identity.affinity != identity.originalAffinity || identity.position != identity.originalPosition || identity.subPosition != identity.originalSubPosition)
            {
                string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
                string path = CharacterIdentityCatalog.Find(directory, "chara_param_");
                var rows = Table(path).Rows.Where(r => r.Name == "CHARA_PARAM_INFO" && r.UInt(1) == character.UInt(0)).ToArray();
                if (rows.Length == 0) throw new InvalidDataException(targetId + " 找不到人物參數。");
                foreach (var row in rows)
                {
                    if (identity.affinity != identity.originalAffinity) Set(row, 2, identity.affinity);
                    if (identity.position != identity.originalPosition) Set(row, 3, identity.position);
                    if (identity.subPosition != identity.originalSubPosition) Set(row, 4, identity.subPosition);
                }
                dirty.Add(path);
            }
            foreach (var locale in identity.locales ?? new List<CharacterLocalizedIdentity>())
            {
                if (!CharacterIdentityCatalog.Locales.Contains(locale.locale)) throw new InvalidDataException("無效的名字語系。");
                PatchText(locale.locale, "chara_text", character.UInt(3), locale.fullName, locale.originalFullName, false);
                PatchText(locale.locale, "chara_text", character.UInt(4), locale.shortName, locale.originalShortName, false);
                PatchText(locale.locale, "chara_text", character.UInt(5), locale.shirtName, locale.originalShirtName, false);
                PatchText(locale.locale, "chara_description_text", character.UInt(19), locale.description, locale.originalDescription, true);
            }
            if (identity.team == identity.originalTeam) return false;
            uint[] equipment = catalog.Equipment(identity.team, identity.sourceSeries, identity.gender, identity.position == 1);
            for (int i = 0; i < equipment.Length; i++) Set(model, 5 + i, equipment[i]);
            return true;
        }
        void UpdateBase(CfgTable.Row character, int field, long value, long original)
        {
            if (value == original) return;
            Set(character, field, value); dirty.Add(bases.Source);
        }
        internal void ApplyVoice(CharacterMakerDocument document)
        {
            if (string.IsNullOrWhiteSpace(document.voiceCharacterId) && string.IsNullOrWhiteSpace(document.voiceAvatarCode)) return;
            var character = bases.Rows.Single(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == document.targetId);
            uint reference = CharacterIdentityCatalog.Get().VoiceReference(document.voiceCharacterId, document.voiceAvatarCode);
            // Zero means this character's own sound bank; a self-reference would loop.
            if (reference == character.UInt(0)) reference = 0;
            UpdateBase(character, 9, reference, character.UInt(9));
        }
        internal bool ApplyFacial(CharacterMakerDocument document, CfgTable.Row model)
        {
            if (string.IsNullOrWhiteSpace(document.head?.sourceId)) return false;
            uint reference = CharacterIdentityCatalog.Get().FacialReference(document.head);
            if (model.UInt(36) == reference) return false;
            Set(model, 36, reference);
            return true;
        }
        void PatchText(string locale, string file, uint id, string value, string original, bool description)
        {
            if (string.Equals(value ?? "", original ?? "", StringComparison.Ordinal)) return;
            if (id == 0) throw new InvalidDataException("人物缺少可寫入的文字 ID。");
            string path = Path.Combine(DumpDataManager.CommonPath, "text", locale, file + ".cfg.bin");
            var table = Table(path); string rowName = description ? "TEXT_INFO" : "NOUN_INFO";
            var row = table.Rows.FirstOrDefault(r => r.Name == rowName && r.UInt(0) == id && (description || r.Int(1) == 0));
            if (row == null)
            {
                var template = table.Rows.FirstOrDefault(r => r.Name == rowName && (description || r.Int(1) == 0));
                if (template == null) throw new InvalidDataException("文字表缺少範本：" + path);
                row = template.Clone(); Set(row, 0, id);
                table.Insert(rowName + "_BEGIN", rowName + "_END", row);
            }
            row.Values[description ? 2 : 5] = value ?? "";
            dirty.Add(path);
        }
        internal bool Write(string outputRoot)
        {
            foreach (string path in dirty)
            {
                var table = string.Equals(path, bases.Source, StringComparison.OrdinalIgnoreCase) ? bases : tables[path];
                byte[] bytes = table.Write(); CfgTable.Read(bytes, "character identity validation");
                string relative = path.Substring(DumpDataManager.CommonPath.Length + 1);
                string destination = Path.Combine(outputRoot, "common", relative);
                Directory.CreateDirectory(Path.GetDirectoryName(destination)); File.WriteAllBytes(destination, bytes);
            }
            return dirty.Count != 0;
        }
    }
}
