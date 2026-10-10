using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using G4Runtime;

namespace G4Composition
{
    // T2B contract from VictoryTool.CfgBin; no dependency on its .NET 10 assembly.
    public sealed class CfgTable
    {
        public sealed class Row
        {
            public uint Hash;
            public string Name;
            public byte[] Types;
            public object[] Values;
            public uint UInt(int i) => unchecked((uint)Convert.ToInt64(Values[i]));
            public long Int(int i) => Convert.ToInt64(Values[i]);
            public string Text(int i) => Values[i] as string;
            public Row Clone() => new Row { Hash = Hash, Name = Name, Types = (byte[])Types.Clone(), Values = (object[])Values.Clone() };
        }
        public readonly List<Row> Rows = new List<Row>();
        public string Source;
        public int Width;
        Encoding encoding;
        byte[] suffix;
        static int Align(int value, int n) => checked((value + n - 1) & -n);
        public static CfgTable Read(byte[] data, string source)
        {
            try
            {
                var b = new G4ModelParser.Binary(data, source); b.Range(0, 32);
                int footer = data.Length - 16;
                if (b.U32(footer) != 0x62327401) throw new InvalidDataException("Not an uncompressed T2B table.");
                int code = b.U16(footer + 6);
                Encoding enc = code == 1 || code == 256 || code == 257 ? new UTF8Encoding(false, true) : code == 0 ? new Windows932Encoding() : throw new InvalidDataException("Unknown T2B encoding.");
                int count = b.Int(0), strings = b.Int(4), length = b.Int(8);
                b.Range(strings, length); if (count > data.Length / 8) throw new InvalidDataException("Invalid entry count.");
                int nt = Align(checked(strings + length), 16); b.Range(nt, 16);
                int size = b.Int(nt), names = b.Int(nt + 4), nameBase = checked(nt + b.Int(nt + 8)), nameLength = b.Int(nt + 12);
                if (nt + (long)size != footer) throw new InvalidDataException("Invalid name table size.");
                b.Range(nt + 16, names * 8L); b.Range(nameBase, nameLength);
                var lookup = new Dictionary<uint, string>();
                Func<int, int, int, string> text = (start, len, rel) =>
                {
                    if (rel < 0 || rel >= len) throw new InvalidDataException("String offset outside table.");
                    int end = start + rel; while (end < start + len && data[end] != 0) end++;
                    if (end == start + len) throw new InvalidDataException("Unterminated table string.");
                    return enc.GetString(data, start + rel, end - start - rel);
                };
                for (int i = 0; i < names; i++) lookup.Add(b.U32(nt + 16 + i * 8), text(nameBase, nameLength, b.Int(nt + 20 + i * 8)));
                foreach (int width in new[] { 4, 8 })
                {
                    try
                    {
                        var table = new CfgTable { Width = width, Source = source, encoding = enc, suffix = data.Skip(nt).ToArray() };
                        int cursor = 16;
                        for (int r = 0; r < count; r++)
                        {
                            uint hash = b.U32(cursor); int n = b.U8(cursor + 4); cursor += 5;
                            var row = new Row { Hash = hash, Name = lookup.TryGetValue(hash, out var name) ? name : hash.ToString("X8"), Types = new byte[n], Values = new object[n] };
                            for (int i = 0; i < n; i++) row.Types[i] = (byte)((b.U8(cursor + i / 4) >> ((i % 4) * 2)) & 3);
                            cursor = Align(cursor + (n + 3) / 4, 4); b.Range(cursor, n * (long)width);
                            if (cursor + (long)n * width > strings) throw new InvalidDataException();
                            for (int i = 0; i < n; i++, cursor += width)
                            {
                                long raw = width == 4 ? BitConverter.ToInt32(data, cursor) : BitConverter.ToInt64(data, cursor);
                                switch (row.Types[i])
                                {
                                    case 0: row.Values[i] = raw == -1 ? null : text(strings, length, checked((int)raw)); break;
                                    case 1: row.Values[i] = raw; break;
                                    case 2: row.Values[i] = width == 4 ? (object)BitConverter.ToSingle(data, cursor) : BitConverter.ToDouble(data, cursor); break;
                                    default: throw new InvalidDataException("Unsupported T2B type.");
                                }
                            }
                            table.Rows.Add(row);
                        }
                        if (Align(cursor, 16) != strings) throw new InvalidDataException();
                        for (; cursor < strings; cursor++) if (data[cursor] != 255) throw new InvalidDataException();
                        return table;
                    }
                    catch (Exception e) when (e is InvalidDataException || e is OverflowException || e is ArgumentException) { }
                }
                throw new InvalidDataException("Unsupported T2B entry width or corrupt entries.");
            }
            catch (Exception e) when (e is InvalidDataException || e is OverflowException || e is ArgumentException) { throw new InvalidDataException(source + ": " + e.Message, e); }
        }
        public void Insert(string begin, string end, params Row[] rows)
        {
            int a = Rows.FindIndex(r => r.Name == begin), z = Rows.FindIndex(r => r.Name == end);
            if (a < 0 || z <= a || Rows[a].Types.Length == 0 || Rows[a].Types[0] != 1) throw new InvalidDataException(Source + ": missing counted list " + begin);
            int logicalCount = rows.Count(r => r.Name == rows[0].Name);
            Rows[a] = Rows[a].Clone(); Rows[a].Values[0] = checked(Rows[a].Int(0) + logicalCount);
            Rows.InsertRange(z, rows);
        }
        public byte[] Write()
        {
            using (var strings = new MemoryStream()) using (var entries = new MemoryStream()) using (var writer = new BinaryWriter(entries))
            {
                var offsets = new Dictionary<string, int>(StringComparer.Ordinal);
                writer.Write(new byte[16]);
                foreach (var row in Rows)
                {
                    if (row.Types.Length != row.Values.Length || row.Values.Length > 255) throw new InvalidDataException("Invalid T2B row.");
                    writer.Write(row.Hash); writer.Write((byte)row.Values.Length);
                    var packed = new byte[(row.Values.Length + 3) / 4];
                    for (int i = 0; i < row.Types.Length; i++) packed[i / 4] |= (byte)(row.Types[i] << ((i % 4) * 2));
                    writer.Write(packed); while (entries.Position % 4 != 0) writer.Write((byte)255);
                    for (int i = 0; i < row.Values.Length; i++)
                    {
                        long value;
                        switch (row.Types[i])
                        {
                            case 0:
                                if (row.Values[i] == null) value = -1;
                                else
                                {
                                    string s = (string)row.Values[i]; if (s.IndexOf('\0') >= 0) throw new InvalidDataException("Embedded NUL in table string.");
                                    if (!offsets.TryGetValue(s, out int offset)) { offset = checked((int)strings.Position); offsets.Add(s, offset); byte[] bytes = encoding.GetBytes(s); strings.Write(bytes, 0, bytes.Length); strings.WriteByte(0); }
                                    value = offset;
                                }
                                break;
                            case 1: value = Convert.ToInt64(row.Values[i]); break;
                            case 2: value = Width == 4 ? BitConverter.ToInt32(BitConverter.GetBytes(Convert.ToSingle(row.Values[i])), 0) : BitConverter.DoubleToInt64Bits(Convert.ToDouble(row.Values[i])); break;
                            default: throw new InvalidDataException("Unknown T2B type.");
                        }
                        if (Width == 4) { if (value < int.MinValue || value > uint.MaxValue) throw new InvalidDataException("T2B value exceeds 32 bits."); writer.Write(unchecked((int)value)); } else writer.Write(value);
                    }
                }
                while (entries.Position % 16 != 0) writer.Write((byte)255);
                int stringOffset = (int)entries.Position; writer.Write(strings.ToArray());
                while (entries.Position % 16 != 0) writer.Write((byte)255);
                writer.Write(suffix); entries.Position = 0;
                writer.Write(Rows.Count); writer.Write(stringOffset); writer.Write((int)strings.Length); writer.Write(offsets.Count);
                var result = entries.ToArray(); var check = Read(result, Source + " (written)");
                if (check.Rows.Count != Rows.Count) throw new InvalidDataException("T2B roundtrip count mismatch.");
                return result;
            }
        }
    }
}
