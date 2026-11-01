using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using G4Composition;
using G4Runtime;

public static class CharacterVoiceExporter
{
    static string suffixRoot;
    static string[] knownVoiceSuffixes;
    static readonly object suffixLock = new object();
    static string[] VoiceSuffixes(string common)
    {
        lock (suffixLock)
        {
            if (suffixRoot == common && knownVoiceSuffixes != null) return knownVoiceSuffixes;
            var suffixes = new HashSet<string>(StringComparer.Ordinal);
            foreach (string language in new[] { "ja", "en" })
            {
                string folder = Path.Combine(common, "sound_asset", language);
                if (!Directory.Exists(folder)) continue;
                foreach (string path in Directory.GetFiles(folder, "*.acb").Where(p =>
                    System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(p), @"^(c\d{8}|scout[A-Z]{3}\d{2})$")))
                    foreach (string suffix in VoiceCueBank.CueSuffixes(path))
                        if (System.Text.RegularExpressions.Regex.IsMatch(suffix, @"^(?:a\d{6}_)?wh[sodk]\d{5}(?:_sp_\d+|_\d+)?$")) suffixes.Add(suffix);
            }
            knownVoiceSuffixes = suffixes.OrderBy(s => s, StringComparer.Ordinal).ToArray(); suffixRoot = common;
            return knownVoiceSuffixes;
        }
    }
    // Register a new generic voice; neither the source character nor its sound bank is overwritten.
    internal static bool Write(IList<CharacterMakerDocument> documents, CharacterCatalog catalog, string stage, string destination, string modNamespace)
    {
        var custom = documents.Where(d => d.voiceOverrides != null && d.voiceOverrides.Any(e => e.muted || e.files != null && e.files.Count > 0)).ToArray();
        if (custom.Length == 0) return false;
        string[] knownSuffixes = custom.Any(d => d.voiceOverrides.Any(e => e.category?.StartsWith("cue:wh", StringComparison.Ordinal) == true && e.files?.Count > 0))
            ? VoiceSuffixes(catalog.Common) : Array.Empty<string>();
        string sourceRegistry = Directory.GetFiles(Path.Combine(catalog.Common, "gamedata", "character"), "general_battle_voice_*.cfg.bin").Single();
        string relative = Path.Combine("common", sourceRegistry.Substring(catalog.Common.Length + 1));
        string existing = Path.Combine(destination, relative);
        byte[] registry = File.ReadAllBytes(File.Exists(existing) ? existing : sourceRegistry);
        foreach (var document in custom)
        {
            if (document.voiceOverrides.GroupBy(e => e.category).Any(g => g.Count() > 1)) throw new InvalidDataException("Duplicate custom voice categories.");
            string key = CharacterMakerExportBatch.NamespacedKey(modNamespace, document.targetId + "/voice");
            string name;
            using (var hash = SHA256.Create()) name = "cmv" + BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").Substring(0, 24).ToLowerInvariant();
            uint id = G4TextureParser.Crc32Utf8(name);
            if (catalog.Base.Rows.Any(r => r.Name == "CHARA_BASE_INFO" && r.UInt(0) == id)) throw new InvalidDataException("Custom voice ID collides with a character.");
            registry = Register(registry, id, name);
            string avatar = string.IsNullOrWhiteSpace(document.voiceAvatarCode) ? null : AvatarRecipeParser.ResolveVoiceName(AvatarRecipeParser.Parse(document.voiceAvatarCode));
            string source = VoicePreviewAudio.ResolveBank(catalog.Common,
                string.IsNullOrWhiteSpace(document.voiceCharacterId) ? document.TemplateId : document.voiceCharacterId, avatar);
            foreach (string language in new[] { "ja", "en" })
            {
                string acb = Path.Combine(catalog.Common, "sound_asset", language, Path.GetFileNameWithoutExtension(source) + ".acb");
                if (!File.Exists(acb)) continue;
                var built = CustomVoiceBank.Build(acb, name, document.voiceOverrides, knownSuffixes);
                string folder = Path.Combine(stage, "common", "sound_asset", language); Directory.CreateDirectory(folder);
                File.WriteAllBytes(Path.Combine(folder, name + ".acb"), built.Acb);
                File.WriteAllBytes(Path.Combine(folder, name + ".awb"), built.Awb);
            }
            var character = catalog.Base.Rows.Single(r => r.Name == "CHARA_BASE_INFO" && r.Text(1) == document.targetId);
            character.Values[9] = unchecked((long)(int)id);
        }
        string output = Path.Combine(stage, relative); Directory.CreateDirectory(Path.GetDirectoryName(output)); File.WriteAllBytes(output, registry);
        return true;
    }

    internal static byte[] Register(byte[] input, uint id, string name)
    {
        var table = new AvatarRecipeParser.Rdbnp(input, "voice registry"); var list = table.GetList("m_generalBattleVoice");
        var rows = new List<(uint Id, string Name)>();
        for (int i = 0; i < list.Count; i++)
        {
            int at = list.Row(i); uint oldId = table.U32(at + list.Field("voiceId")); string oldName = table.String(at + list.Field("voiceIdString"));
            if (oldId == id && oldName != name || oldName == name && oldId != id) throw new InvalidDataException("Custom voice registry collision: " + name);
            if (oldId != id) rows.Add((oldId, oldName));
        }
        rows.Add((id, name)); rows.Sort((a, b) => a.Id.CompareTo(b.Id));
        int U16(int at) => BitConverter.ToUInt16(input, at);
        int baseline = U16(10) * 4, roots = baseline + U16(44) * 4, values = baseline + U16(54) * 4;
        int strings = baseline + BitConverter.ToInt32(input, 56);
        if (U16(46) != 1 || list.Row(0) != values || strings != values + list.Count * 8)
            throw new InvalidDataException("Unsupported generic voice registry layout.");
        // Keep the original string pool and all its relative references; append just the new names.
        byte[] oldStrings = input.Skip(strings).ToArray();
        var pool = new MemoryStream(); pool.Write(oldStrings, 0, oldStrings.Length);
        var references = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < list.Count; i++) references[table.String(list.Row(i) + list.Field("voiceIdString"))] = BitConverter.ToInt32(input, list.Row(i) + list.Field("voiceIdString"));
        foreach (var row in rows)
            if (!references.ContainsKey(row.Name)) { references[row.Name] = checked((int)pool.Position); byte[] b = Encoding.UTF8.GetBytes(row.Name); pool.Write(b, 0, b.Length); pool.WriteByte(0); }
        while (pool.Position % 4 != 0) pool.WriteByte(0);
        using (var output = new MemoryStream())
        using (var writer = new BinaryWriter(output))
        {
            writer.Write(input, 0, values);
            foreach (var row in rows) { writer.Write(row.Id); writer.Write(references[row.Name]); }
            int newStrings = checked((int)output.Position); writer.Write(pool.ToArray());
            output.Position = roots + 12; writer.Write(rows.Count);
            output.Position = 12; writer.Write(checked((int)output.Length - baseline));
            output.Position = 56; writer.Write(newStrings - baseline);
            byte[] result = output.ToArray();
            var check = new AvatarRecipeParser.Rdbnp(result, "custom voice registry validation");
            if (check.GetList("m_generalBattleVoice").Count != rows.Count) throw new InvalidDataException("Voice registry validation failed.");
            return result;
        }
    }
}
