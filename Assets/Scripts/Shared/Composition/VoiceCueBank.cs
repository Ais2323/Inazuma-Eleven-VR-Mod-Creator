using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace G4Composition
{
    // ACB cue indices, waveform indices and AWB stream IDs are separate namespaces.
    public sealed class VoiceCueBank
    {
        public sealed class Clip
        {
            public string Name;
            public int CueId, WaveId;
        }

        readonly Dictionary<string, List<Dictionary<string, object>>> tables =
            new Dictionary<string, List<Dictionary<string, object>>>();
        readonly Dictionary<string, object> root;

        public VoiceCueBank(string path) { root = ReadUtf(File.ReadAllBytes(path))[0]; }

        public static IEnumerable<string> CueSuffixes(string path)
        {
            var header = ReadUtf(File.ReadAllBytes(path))[0];
            return ReadUtf((byte[])header["CueNameTable"]).Select(row => {
                string name = (string)row["CueName"]; int underscore = name.IndexOf('_');
                return underscore < 0 ? name : name.Substring(underscore + 1);
            }).ToArray();
        }

        public string[] AvailableCueNames()
        {
            var cues = Table("CueTable");
            return Table("CueNameTable").Where(row => {
                var cue = cues[Number(row, "CueIndex")];
                return !cue.ContainsKey("NumRelatedWaveforms") || Number(cue, "NumRelatedWaveforms") > 0;
            }).Select(row => (string)row["CueName"]).ToArray();
        }

        public List<Clip> Group(string group)
            => ReadClips(suffix => suffix.StartsWith(group, StringComparison.Ordinal) && suffix.Length > group.Length && char.IsDigit(suffix[group.Length]));

        public List<Clip> Cues(IEnumerable<string> suffixes)
        {
            var selected = new HashSet<string>(suffixes, StringComparer.Ordinal);
            return ReadClips(selected.Contains);
        }

        public List<Clip> Category(string category) => ReadClips(suffix => NormalVoiceCategories.Matches(category, suffix));

        List<Clip> ReadClips(Func<string, bool> matches)
        {
            var clips = new List<Clip>();
            foreach (var named in Table("CueNameTable").OrderBy(r => (string)r["CueName"], StringComparer.Ordinal))
            {
                string name = (string)named["CueName"];
                int separator = name.IndexOf('_');
                string suffix = separator < 0 ? name : name.Substring(separator + 1);
                if (!matches(suffix)) continue;
                var cue = Row("CueTable", Number(named, "CueIndex"));
                // Empty placeholders can contain non-audio references (e.g. control/link nodes).
                if (cue.ContainsKey("NumRelatedWaveforms") && Number(cue, "NumRelatedWaveforms") == 0) continue;
                var waves = new List<int>();
                Resolve(Number(cue, "ReferenceType"), Number(cue, "ReferenceIndex"), waves, new HashSet<string>());
                foreach (int wave in waves.Distinct())
                    clips.Add(new Clip { Name = name, CueId = Number(cue, "CueId"), WaveId = wave });
            }
            return clips;
        }

        void Resolve(int type, int index, List<int> waves, HashSet<string> visiting)
        {
            string key = type + ":" + index;
            if (!visiting.Add(key)) throw new InvalidDataException("Cyclic ACB reference: " + key);
            try
            {
                if (type == 1)
                {
                    var wave = Row("WaveformTable", index);
                    if (Number(wave, "Streaming") == 0)
                        throw new InvalidDataException("Embedded ACB waveform is not supported by voice preview.");
                    waves.Add(Number(wave, "StreamAwbId"));
                }
                else if (type == 2)
                {
                    byte[] refs = Blob(Row("SynthTable", index), "ReferenceItems");
                    for (int p = 0; p + 4 <= refs.Length; p += 4)
                        if (U16(refs, p) != 0) Resolve(U16(refs, p), U16(refs, p + 2), waves, visiting);
                }
                else if (type == 3)
                {
                    var sequence = Row("SequenceTable", index);
                    byte[] tracks = Blob(sequence, "TrackIndex");
                    int count = Number(sequence, "NumTracks");
                    if (tracks.Length < count * 2) throw new InvalidDataException("Invalid ACB track indices.");
                    for (int p = 0; p < count * 2; p += 2)
                    {
                        var track = Row("TrackTable", U16(tracks, p));
                        int eventIndex = Number(track, "EventIndex");
                        if (eventIndex == 65535) continue;
                        byte[] command = Blob(Row("TrackEventTable", eventIndex), "Command");
                        for (int c = 0; c + 3 <= command.Length;)
                        {
                            int opcode = U16(command, c), size = command[c + 2]; c += 3;
                            if (c + size > command.Length) throw new InvalidDataException("Invalid ACB event command.");
                            if (opcode == 2000 || opcode == 2003)
                            {
                                if (size < 4) throw new InvalidDataException("Invalid ACB note event.");
                                Resolve(U16(command, c), U16(command, c + 2), waves, visiting);
                            }
                            c += size;
                        }
                    }
                }
                else throw new InvalidDataException("Unsupported ACB reference type: " + type);
            }
            finally { visiting.Remove(key); }
        }

        List<Dictionary<string, object>> Table(string name)
        {
            if (!tables.TryGetValue(name, out var result))
            {
                if (!root.TryGetValue(name, out var blob) || !(blob is byte[] data) || data.Length == 0)
                    throw new InvalidDataException("Missing ACB table: " + name);
                tables[name] = result = ReadUtf(data);
            }
            return result;
        }
        Dictionary<string, object> Row(string table, int index)
        {
            var rows = Table(table);
            if (index < 0 || index >= rows.Count) throw new InvalidDataException("Invalid ACB index: " + table + "/" + index);
            return rows[index];
        }
        static int Number(Dictionary<string, object> row, string key) => checked(Convert.ToInt32(row[key]));
        static byte[] Blob(Dictionary<string, object> row, string key) => (byte[])row[key];
        static int U16(byte[] b, int p) => b[p] << 8 | b[p + 1];
        static uint U32(byte[] b, int p) => (uint)b[p] << 24 | (uint)b[p + 1] << 16 | (uint)b[p + 2] << 8 | b[p + 3];

        sealed class Column { public string Name; public int Storage, Type; public object Constant; }
        internal static List<Dictionary<string, object>> ReadUtf(byte[] data)
        {
            if (data.Length < 32 || Encoding.ASCII.GetString(data, 0, 4) != "@UTF")
                throw new InvalidDataException("Invalid CRI UTF table.");
            int rowsAt = checked(U16(data, 10) + 8), strings = checked((int)U32(data, 12) + 8),
                blobs = checked((int)U32(data, 16) + 8), width = U16(data, 26), count = checked((int)U32(data, 28));
            if (count > 1000000 || (long)rowsAt + (long)count * width > data.Length)
                throw new InvalidDataException("CRI UTF rows exceed table bounds.");
            string StringAt(int offset)
            {
                int start = checked(strings + offset), end = start;
                while (end < data.Length && data[end] != 0) end++;
                if (start < strings || end == data.Length) throw new InvalidDataException("Invalid CRI UTF string.");
                return Encoding.UTF8.GetString(data, start, end - start);
            }
            object Value(int type, ref int p)
            {
                int size = type < 4 ? (type < 2 ? 1 : 2) : type == 6 || type == 7 || type == 9 || type == 11 ? 8 : 4;
                if (p < 0 || p > data.Length - size) throw new InvalidDataException("Invalid CRI UTF value.");
                int at = p; p += size;
                switch (type)
                {
                    case 0: return data[at];
                    case 1: return (sbyte)data[at];
                    case 2: return U16(data, at);
                    case 3: return (short)U16(data, at);
                    case 4: return U32(data, at);
                    case 5: return (int)U32(data, at);
                    case 6: return (ulong)U32(data, at) << 32 | U32(data, at + 4);
                    case 7: return unchecked((long)((ulong)U32(data, at) << 32 | U32(data, at + 4)));
                    case 8: case 9:
                        byte[] number = new byte[size]; Array.Copy(data, at, number, 0, size);
                        if (BitConverter.IsLittleEndian) Array.Reverse(number);
                        return type == 8 ? (object)BitConverter.ToSingle(number, 0) : BitConverter.ToDouble(number, 0);
                    case 10: return StringAt(checked((int)U32(data, at)));
                    case 11:
                        int start = checked(blobs + (int)U32(data, at)), length = checked((int)U32(data, at + 4));
                        if (start < blobs || start > data.Length - length) throw new InvalidDataException("Invalid CRI UTF blob.");
                        byte[] blob = new byte[length]; Array.Copy(data, start, blob, 0, length); return blob;
                    default: throw new InvalidDataException("Unsupported CRI UTF type: " + type);
                }
            }
            int cursor = 32;
            var columns = new List<Column>();
            for (int c = 0; c < U16(data, 24); c++)
            {
                int flags = data[cursor++];
                string name = StringAt(checked((int)U32(data, cursor))); cursor += 4;
                var column = new Column { Name = name, Storage = flags & 240, Type = flags & 15 };
                if (column.Storage == 48) column.Constant = Value(column.Type, ref cursor);
                else if (column.Storage == 16) column.Constant = column.Type == 11 ? (object)Array.Empty<byte>() : column.Type == 10 ? "" : (object)0;
                else if (column.Storage != 80) throw new InvalidDataException("Unsupported CRI UTF storage.");
                columns.Add(column);
            }
            var result = new List<Dictionary<string, object>>();
            for (int r = 0; r < count; r++)
            {
                cursor = rowsAt + r * width;
                var row = new Dictionary<string, object>();
                foreach (var column in columns)
                    row[column.Name] = column.Storage == 80 ? Value(column.Type, ref cursor) : column.Constant;
                result.Add(row);
            }
            return result;
        }
    }
}
