using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace G4Composition
{
    public static partial class CustomVoiceBank
    {
        // Keep the original control/volume command tables and their stable indices,
        // but copy only audio graph nodes reachable from the retained Cue names.
        static void CompactVoiceGraph(Dictionary<string, List<Dictionary<string, object>>> tables, SortedDictionary<int, byte[]> streams)
        {
            string[] kinds = { "CueTable", "SequenceTable", "TrackTable", "TrackEventTable", "SynthTable", "WaveformTable" };
            var output = kinds.ToDictionary(k => k, k => new List<Dictionary<string, object>>());
            var mappings = kinds.ToDictionary(k => k, k => new Dictionary<int, int>());
            var waveIds = new HashSet<int>();
            int U16(byte[] data, int at) => data[at] << 8 | data[at + 1];
            void Put16(byte[] data, int at, int value)
            {
                if (value < 0 || value > 65534) throw new InvalidDataException("Filtered voice index exceeds native limits.");
                data[at] = (byte)(value >> 8); data[at + 1] = (byte)value;
            }
            int Reference(int type, int index)
            {
                if (type == 1) return Map("WaveformTable", index);
                if (type == 2) return Map("SynthTable", index);
                if (type == 3) return Map("SequenceTable", index);
                throw new InvalidDataException("Unsupported audio reference in filtered voice: " + type);
            }
            int emptySequence = -1;
            int EmptySequence()
            {
                if (emptySequence >= 0) return emptySequence;
                var row = Copy(tables["SequenceTable"].First());
                emptySequence = output["SequenceTable"].Count;
                row["NumTracks"] = 0; row["TrackIndex"] = Array.Empty<byte>(); row["TrackValues"] = Array.Empty<byte>(); row["Type"] = 0;
                row["ControlWorkArea1"] = emptySequence;
                row["NumPlaybackTrackNoHistories"] = Pair(0, emptySequence).Skip(2).ToArray();
                output["SequenceTable"].Add(row); return emptySequence;
            }
            int Map(string kind, int index)
            {
                if (mappings[kind].TryGetValue(index, out int existing)) return existing;
                if (index < 0 || index >= tables[kind].Count) throw new InvalidDataException("Invalid retained voice node: " + kind);
                int result = output[kind].Count;
                var row = Copy(tables[kind][index]); mappings[kind].Add(index, result); output[kind].Add(row);
                if (kind == "CueTable")
                {
                    if (row.ContainsKey("NumRelatedWaveforms") && N(row, "NumRelatedWaveforms") == 0)
                    { row["ReferenceType"] = 3; row["ReferenceIndex"] = EmptySequence(); }
                    else row["ReferenceIndex"] = Reference(N(row, "ReferenceType"), N(row, "ReferenceIndex"));
                }
                else if (kind == "SequenceTable")
                {
                    byte[] indices = ((byte[])row["TrackIndex"]).ToArray(); int count = N(row, "NumTracks");
                    if (indices.Length < count * 2) throw new InvalidDataException("Invalid retained voice tracks.");
                    for (int p = 0; p < count * 2; p += 2) Put16(indices, p, Map("TrackTable", U16(indices, p)));
                    row["TrackIndex"] = indices; row["ControlWorkArea1"] = result;
                    row["NumPlaybackTrackNoHistories"] = Pair(0, result).Skip(2).ToArray();
                }
                else if (kind == "TrackTable")
                {
                    int eventIndex = N(row, "EventIndex");
                    if (eventIndex != 65535) row["EventIndex"] = Map("TrackEventTable", eventIndex);
                }
                else if (kind == "TrackEventTable")
                {
                    byte[] command = ((byte[])row["Command"]).ToArray();
                    for (int at = 0; at + 3 <= command.Length;)
                    {
                        int op = U16(command, at), size = command[at + 2]; at += 3;
                        if (at + size > command.Length) throw new InvalidDataException("Invalid retained voice event.");
                        if (op == 2000 || op == 2003 || op == 2004 || op == 2002)
                        {
                            if (size < 4) throw new InvalidDataException("Invalid retained voice note.");
                            Put16(command, at + 2, Reference(U16(command, at), U16(command, at + 2)));
                        }
                        at += size;
                    }
                    row["Command"] = command;
                }
                else if (kind == "SynthTable")
                {
                    byte[] references = ((byte[])row["ReferenceItems"]).ToArray();
                    for (int p = 0; p + 4 <= references.Length; p += 4)
                    {
                        int type = U16(references, p); if (type == 0) break;
                        Put16(references, p + 2, Reference(type, U16(references, p + 2)));
                    }
                    row["ReferenceItems"] = references;
                    if (row.ContainsKey("ControlWorkArea1")) row["ControlWorkArea1"] = result;
                    if (row.ContainsKey("ControlWorkArea2")) row["ControlWorkArea2"] = result;
                }
                else if (kind == "WaveformTable")
                {
                    if (N(row, "Streaming") == 0) throw new InvalidDataException("Embedded waveforms cannot be copied into a filtered streaming voice.");
                    int id = N(row, "StreamAwbId");
                    if (!streams.ContainsKey(id)) throw new InvalidDataException("Retained voice references a missing stream: " + id);
                    waveIds.Add(id);
                }
                return result;
            }
            // CueNameTable is sorted by name, while the native CueTable is sorted
            // by numeric CueId. Do not let name traversal reorder the ID lookup.
            foreach (int index in tables["CueNameTable"].Select(named => N(named, "CueIndex")).Distinct()
                .OrderBy(index => N(tables["CueTable"][index], "CueId")))
                Map("CueTable", index);
            foreach (var named in tables["CueNameTable"]) named["CueIndex"] = mappings["CueTable"][N(named, "CueIndex")];
            foreach (string kind in kinds) tables[kind] = output[kind];
            foreach (int id in streams.Keys.Where(id => !waveIds.Contains(id)).ToArray()) streams.Remove(id);
        }
    }
}
