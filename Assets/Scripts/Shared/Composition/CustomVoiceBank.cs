using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace G4Composition
{
    public static partial class CustomVoiceBank
    {
        public sealed class Built { public byte[] Acb, Awb; }
        static int N(Dictionary<string, object> row, string key) => Convert.ToInt32(row[key]);
        static Dictionary<string, object> Copy(Dictionary<string, object> row) => new Dictionary<string, object>(row);
        static byte[] Pair(int a, int b) => new[] { (byte)(a >> 8), (byte)a, (byte)(b >> 8), (byte)b };

        public static Built Build(string acbPath, string newName, IList<CharacterVoiceOverride> edits, IEnumerable<string> knownSuffixes = null)
        {
            byte[] acb = File.ReadAllBytes(acbPath);
            var header = VoiceCueBank.ReadUtf(acb)[0];
            var tables = new Dictionary<string, List<Dictionary<string, object>>>();
            List<Dictionary<string, object>> Table(string name)
            {
                if (!tables.TryGetValue(name, out var table)) tables.Add(name, table = VoiceCueBank.ReadUtf((byte[])header[name]));
                return table;
            }
            var cues = Table("CueTable"); var names = Table("CueNameTable"); var sequences = Table("SequenceTable");
            var tracks = Table("TrackTable"); var events = Table("TrackEventTable"); var synths = Table("SynthTable");
            var waves = Table("WaveformTable"); var commands = Table("SeqCommandTable");
            byte[] originalAwb = File.ReadAllBytes(Path.ChangeExtension(acbPath, ".awb"));
            var streams = ReadAwb(originalAwb);
            var sourceSequence = sequences.First(s => N(s, "NumTracks") > 0 && ((byte[])s["TrackIndex"]).Length >= 2);
            var sourceTrackIndices = (byte[])sourceSequence["TrackIndex"];
            var sourceTrack = tracks[sourceTrackIndices[0] << 8 | sourceTrackIndices[1]];
            var sourceSynth = synths.First(s => ((byte[])s["ReferenceItems"]).Length == 4 && ((byte[])s["ReferenceItems"])[1] == 1);
            byte[] sourceRef = (byte[])sourceSynth["ReferenceItems"];
            var sourceWave = waves[sourceRef[2] << 8 | sourceRef[3]];
            string Suffix(string name) { int underscore = name.IndexOf('_'); return underscore < 0 ? name : name.Substring(underscore + 1); }
            names.RemoveAll(row => !NormalVoiceCategories.KeepInheritedCue(Suffix((string)row["CueName"])));

            byte[] silence = null;
            foreach (var edit in edits.Where(e => e.muted || e.files != null && e.files.Count > 0))
            {
                string[] selected = NormalVoiceCategories.Cues(edit.category);
                var affected = names.Where(r => NormalVoiceCategories.Matches(edit.category, Suffix((string)r["CueName"]))).ToList();
                if (edit.muted && affected.Count == 0) continue;
                var files = edit.muted ? new List<CharacterVoiceFile> { new CharacterVoiceFile {
                    hcaBase64 = Convert.ToBase64String(silence ??= VoiceAudioEncoding.Encode(new float[48], 48000, 1))
                } } : edit.files;
                // A new technique/summon may never have been voiced by the source character.
                // Give it its own Cue rather than requiring an existing recording.
                bool canCreate = edit.category.StartsWith("cue:", StringComparison.Ordinal) || edit.category == "armed" || edit.category == "wmm" || edit.category == "wmt";
                var required = selected.Concat(edit.category.StartsWith("cue:", StringComparison.Ordinal) ?
                    (knownSuffixes ?? Array.Empty<string>()).Where(s => NormalVoiceCategories.Matches(edit.category, s)) : Array.Empty<string>()).Distinct();
                foreach (string suffix in canCreate ? required : Array.Empty<string>())
                {
                    if (names.Any(r => Suffix((string)r["CueName"]) == suffix)) continue;
                    if (cues.Count > 65534) throw new InvalidDataException("Too many voice cues.");
                    var cue = Copy(cues.First(c => N(c, "ReferenceType") == 3));
                    cue["CueId"] = cues.Max(c => N(c, "CueId")) + 1;
                    var named = new Dictionary<string, object> { ["CueName"] = newName + "_" + suffix, ["CueIndex"] = cues.Count };
                    cues.Add(cue); names.Add(named); affected.Add(named);
                }
                if (affected.Count == 0) throw new InvalidDataException("Voice source has no cues for " + edit.category);
                var sequence = Copy(sourceSequence); var newTracks = new List<byte>(); int maxLength = 0;
                foreach (var file in files)
                {
                    byte[] hca = VoiceAudioEncoding.ToGameFormat(Convert.FromBase64String(file.hcaBase64));
                    var info = HcaInfo(hca);
                    int streamId = streams.Count == 0 ? 0 : streams.Keys.Max() + 1;
                    if (streamId > 65534 || waves.Count > 65534 || synths.Count > 65534 || events.Count > 65534 || tracks.Count > 65534)
                        throw new InvalidDataException("Voice bank exceeds native index limits.");
                    streams.Add(streamId, hca);
                    var wave = Copy(sourceWave);
                    wave["StreamAwbId"] = streamId; wave["MemoryAwbId"] = 65535; wave["Streaming"] = 1; wave["EncodeType"] = 2;
                    wave["NumChannels"] = info.Channels; wave["SamplingRate"] = info.Rate; wave["NumSamples"] = info.Samples;
                    // Keep the native waveform flag used by character voices.
                    // The HCA payload itself determines whether it contains a loop.
                    int waveIndex = waves.Count; waves.Add(wave);
                    var synth = Copy(sourceSynth); synth["ReferenceItems"] = Pair(1, waveIndex);
                    if (synth.ContainsKey("ControlWorkArea1")) synth["ControlWorkArea1"] = synths.Count;
                    if (synth.ContainsKey("ControlWorkArea2")) synth["ControlWorkArea2"] = synths.Count;
                    int synthIndex = synths.Count; synths.Add(synth);
                    var command = new byte[] { 7, 208, 4 }.Concat(Pair(2, synthIndex)).Concat(new byte[3]).ToArray();
                    int eventIndex = events.Count; events.Add(new Dictionary<string, object> { ["Command"] = command });
                    var track = Copy(sourceTrack); track["EventIndex"] = eventIndex;
                    newTracks.AddRange(new[] { (byte)(tracks.Count >> 8), (byte)tracks.Count }); tracks.Add(track);
                    maxLength = Math.Max(maxLength, (int)Math.Ceiling(info.Samples * 1000.0 / info.Rate));
                }
                int sequenceIndex = sequences.Count;
                if (sequenceIndex > 65534 || files.Count > 65534) throw new InvalidDataException("Too many voice variants.");
                // Native character cues use random-no-repeat even for one track.
                sequence["Type"] = 4; sequence["NumTracks"] = files.Count;
                sequence["TrackIndex"] = newTracks.ToArray(); sequence["TrackValues"] = Enumerable.Repeat(new byte[] { 0, 100 }, files.Count).SelectMany(x => x).ToArray();
                sequence["ControlWorkArea1"] = sequenceIndex; sequence["NumPlaybackTrackNoHistories"] = new[] { (byte)(sequenceIndex >> 8), (byte)sequenceIndex };
                var sequenceCommand = Copy(commands[N(sourceSequence, "CommandIndex")]);
                byte[] bytes = ((byte[])sequenceCommand["Command"]).ToArray();
                for (int at = 0; at + 3 <= bytes.Length;)
                {
                    int op = bytes[at] << 8 | bytes[at + 1], size = bytes[at + 2]; at += 3;
                    if (at + size > bytes.Length) throw new InvalidDataException("Invalid voice sequence command.");
                    if (op == 79)
                    {
                        if (size != 5) throw new InvalidDataException("Unsupported voice limit command.");
                        int limit = N(header, "NumCueLimitListWorks");
                        if (limit > 65534) throw new InvalidDataException("Too many voice limit groups.");
                        bytes[at + 2] = (byte)(limit >> 8); bytes[at + 3] = (byte)limit;
                        header["NumCueLimitListWorks"] = limit + 1;
                        header["NumCueLimitNodeWorks"] = N(header, "NumCueLimitNodeWorks") + 1;
                        header["CueLimitWorkTable"] = ((byte[])header["CueLimitWorkTable"]).Concat(new byte[64]).ToArray();
                    }
                    at += size;
                }
                sequenceCommand["Command"] = bytes; sequence["CommandIndex"] = commands.Count; commands.Add(sequenceCommand);
                sequences.Add(sequence);
                foreach (var named in affected)
                {
                    var cue = cues[N(named, "CueIndex")];
                    cue["ReferenceType"] = 3; cue["ReferenceIndex"] = sequenceIndex; cue["Length"] = maxLength;
                    cue["NumRelatedWaveforms"] = files.Count;
                }
            }
            foreach (var named in names) named["CueName"] = newName + "_" + Suffix((string)named["CueName"]);
            names.Sort((a, b) => StringComparer.Ordinal.Compare((string)a["CueName"], (string)b["CueName"]));
            CompactVoiceGraph(tables, streams);
            ValidateCueBindings(tables, newName);
            // Native Victory Road banks use dense AWB IDs. Filtering removes
            // recordings, so rewrite both IDs and waveform references together.
            var streamIds = streams.Keys.Select((oldId, newId) => new { oldId, newId })
                .ToDictionary(p => p.oldId, p => p.newId);
            foreach (var wave in tables["WaveformTable"])
                wave["StreamAwbId"] = streamIds[N(wave, "StreamAwbId")];
            streams = new SortedDictionary<int, byte[]>(streams.ToDictionary(p => streamIds[p.Key], p => p.Value));
            byte[] awb = WriteAwb(streams, originalAwb, out byte[] awbHeader);
            var awbHash = Table("StreamAwbHash")[0];
            awbHash["Name"] = newName;
            using (var md5 = MD5.Create()) awbHash["Hash"] = md5.ComputeHash(awb);
            Table("StreamAwbAfs2Header")[0]["Header"] = awbHeader;
            foreach (var table in tables) header[table.Key] = CriUtfWriter.Write((byte[])header[table.Key], table.Value);
            header["Name"] = newName;
            if (header.ContainsKey("AcbGuid")) using (var md5 = MD5.Create()) header["AcbGuid"] = md5.ComputeHash(Encoding.UTF8.GetBytes(newName));
            return new Built { Acb = CriUtfWriter.Write(acb, new List<Dictionary<string, object>> { header }), Awb = awb };
        }

        static void ValidateCueBindings(Dictionary<string, List<Dictionary<string, object>>> tables, string bankName)
        {
            // The game enumerates a primary name for each Cue. Diagnostic aliases
            // sharing CueIndex hid the name requested by normal/technique events.
            var names = tables["CueNameTable"];
            if (names.Any(r => !((string)r["CueName"]).StartsWith(bankName + "_", StringComparison.Ordinal)) ||
                names.GroupBy(r => (string)r["CueName"]).Any(g => g.Count() > 1) ||
                names.GroupBy(r => N(r, "CueIndex")).Any(g => g.Count() > 1))
                throw new InvalidDataException("Custom voice cues require one primary bank-prefixed name per Cue.");
            foreach (string kind in new[] { "SequenceTable", "SynthTable" })
                for (int i = 0; i < tables[kind].Count; i++)
                    foreach (string field in new[] { "ControlWorkArea1", "ControlWorkArea2" })
                        if (tables[kind][i].ContainsKey(field) && N(tables[kind][i], field) != i)
                            throw new InvalidDataException("Invalid custom voice workspace: " + kind + "." + field);
        }

        public static (int Channels, int Rate, int Samples) HcaInfo(byte[] hca)
        {
            int U16(int p) => hca[p] << 8 | hca[p + 1];
            int U32(int p) => checked((int)((uint)hca[p] << 24 | (uint)hca[p + 1] << 16 | (uint)hca[p + 2] << 8 | hca[p + 3]));
            if (hca.Length < 24 || Encoding.ASCII.GetString(hca, 0, 4) != "HCA\0" || Encoding.ASCII.GetString(hca, 8, 4) != "fmt\0")
                throw new InvalidDataException("Custom voice must be an unencrypted HCA.");
            int samples = checked(U32(16) * 1024 - U16(20) - U16(22));
            int rate = U32(12) & 0xFFFFFF, channels = hca[12];
            if (rate < 8000 || channels < 1 || channels > 2 || samples < 1 || samples > 30L * rate)
                throw new InvalidDataException("Invalid custom HCA duration or format.");
            return (channels, rate, samples);
        }

        internal static SortedDictionary<int, byte[]> ReadAwb(byte[] data)
        {
            using (var reader = new BinaryReader(new MemoryStream(data)))
            {
                if (Encoding.ASCII.GetString(reader.ReadBytes(4)) != "AFS2") throw new InvalidDataException("Invalid AWB.");
                reader.ReadByte(); int offsetSize = reader.ReadByte(), idSize = reader.ReadByte(); reader.ReadByte();
                int count = reader.ReadInt32(), alignment = reader.ReadUInt16(); reader.ReadUInt16();
                if (count < 0 || count > 65535 || alignment < 1 || offsetSize < 1 || offsetSize > 8 || idSize < 1 || idSize > 4) throw new InvalidDataException("Invalid AWB header.");
                long Number(int size) { long n = 0; for (int i = 0; i < size; i++) n |= (long)reader.ReadByte() << (8 * i); return n; }
                int[] ids = Enumerable.Range(0, count).Select(_ => checked((int)Number(idSize))).ToArray();
                long[] offsets = Enumerable.Range(0, count + 1).Select(_ => Number(offsetSize)).ToArray();
                var result = new SortedDictionary<int, byte[]>();
                for (int i = 0; i < count; i++)
                {
                    long start = (offsets[i] + alignment - 1) / alignment * alignment;
                    int length = checked((int)(offsets[i + 1] - start));
                    if (start < reader.BaseStream.Position || length < 0 || start > data.Length - length) throw new InvalidDataException("Invalid AWB stream range.");
                    var bytes = new byte[length]; Array.Copy(data, start, bytes, 0, length); result.Add(ids[i], bytes);
                }
                return result;
            }
        }

        static byte[] WriteAwb(SortedDictionary<int, byte[]> streams, byte[] original, out byte[] header)
        {
            int alignment = BitConverter.ToUInt16(original, 12), idSize = original[6];
            int position = checked(16 + streams.Count * idSize + (streams.Count + 1) * 4);
            var offsets = new List<int> { position };
            foreach (var wave in streams.Values) { position = checked((position + alignment - 1) / alignment * alignment + wave.Length); offsets.Add(position); }
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(Encoding.ASCII.GetBytes("AFS2")); writer.Write(new byte[] { original[4], 4, (byte)idSize, original[7] });
                writer.Write(streams.Count); writer.Write((ushort)alignment); writer.Write(BitConverter.ToUInt16(original, 14));
                foreach (int id in streams.Keys)
                {
                    if (idSize < 4 && (uint)id >= (1u << (idSize * 8))) throw new InvalidDataException("AWB stream ID exceeds native field size.");
                    for (int i = 0; i < idSize; i++) writer.Write((byte)(id >> (i * 8)));
                }
                foreach (int offset in offsets) writer.Write(offset);
                byte[] rawHeader = output.ToArray(); header = new byte[(rawHeader.Length + 7) / 8 * 8]; Array.Copy(rawHeader, header, rawHeader.Length);
                foreach (byte[] wave in streams.Values) { while (output.Position % alignment != 0) writer.Write((byte)0); writer.Write(wave); }
                return output.ToArray();
            }
        }
    }
}
