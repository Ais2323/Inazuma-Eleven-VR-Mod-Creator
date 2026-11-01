using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace G4Composition
{
    // Match the native CRI layout: retain column storage, null blobs, and
    // 32-byte alignment for the ACB header's embedded tables.
    internal static class CriUtfWriter
    {
        public static byte[] Write(byte[] original, List<Dictionary<string, object>> rows)
        {
            int U16(int p) => original[p] << 8 | original[p + 1];
            int U32(int p) => checked((int)((uint)original[p] << 24 | (uint)original[p + 1] << 16 | (uint)original[p + 2] << 8 | original[p + 3]));
            int stringsAt = U32(12) + 8;
            string Text(int offset) { int p = stringsAt + offset, end = p; while (original[end] != 0) end++; return Encoding.UTF8.GetString(original, p, end - p); }
            object Value(int type, int at)
            {
                if (type == 10) return Text(U32(at));
                if (type == 11) {
                    int start = U32(16) + 8 + U32(at), length = U32(at + 4);
                    var blob = new byte[length]; Array.Copy(original, start, blob, 0, length); return blob;
                }
                byte[] bytes = original.Skip(at).Take(Size(type)).ToArray();
                if (type == 8 || type == 9) {
                    if (BitConverter.IsLittleEndian) Array.Reverse(bytes);
                    return type == 8 ? (object)BitConverter.ToSingle(bytes, 0) : BitConverter.ToDouble(bytes, 0);
                }
                ulong n = 0; foreach (byte b in bytes) n = n << 8 | b;
                if ((type & 1) == 0) return n;
                int shift = 64 - bytes.Length * 8; return unchecked((long)(n << shift)) >> shift;
            }
            bool Equal(object a, object b) => a is byte[] ba && b is byte[] bb ? ba.SequenceEqual(bb) :
                a is string || b is string ? Equals(a, b) : Convert.ToDecimal(a) == Convert.ToDecimal(b);
            var columns = new List<(string Name, int Type, int Storage, object Constant)>();
            int cursor = 32;
            for (int i = 0; i < U16(24); i++)
            {
                int flag = original[cursor++], type = flag & 15;
                string name = Text(U32(cursor)); cursor += 4;
                int storage = flag & 240;
                object constant = storage == 48 ? Value(type, cursor) : type == 11 ? (object)Array.Empty<byte>() : type == 10 ? "" : (object)0;
                if (storage == 48) cursor += Size(type);
                if (storage != 80 && rows.Any(row => !Equal(row[name], constant))) storage = 80;
                columns.Add((name, type, storage, constant));
            }
            var strings = new MemoryStream(); var blobs = new MemoryStream();
            int AddString(string value)
            {
                value ??= "";
                int offset = checked((int)strings.Position); byte[] b = Encoding.UTF8.GetBytes(value);
                strings.Write(b, 0, b.Length); strings.WriteByte(0); return offset;
            }
            int tableName = AddString(Text(U32(20)));
            using (var output = new MemoryStream())
            using (var writer = new BinaryWriter(output))
            {
                writer.Write(new byte[32]);
                bool top = Text(U32(20)) == "Header";
                void WriteValue(int type, object value)
                {
                    switch (type)
                    {
                        case 10: Be(writer, AddString((string)value), 4); break;
                        case 11:
                            byte[] blob = (byte[])value;
                            if (blob.Length == 0) { Be(writer, 0, 8); break; }
                            if (top && blobs.Position > 0) { blobs.WriteByte(0); while (blobs.Position % 32 != 0) blobs.WriteByte(0); }
                            Be(writer, blobs.Position, 4); Be(writer, blob.Length, 4); blobs.Write(blob, 0, blob.Length); break;
                        case 6: case 7: Be(writer, value is ulong u ? unchecked((long)u) : Convert.ToInt64(value), 8); break;
                        case 8: case 9:
                            byte[] number = type == 8 ? BitConverter.GetBytes(Convert.ToSingle(value)) : BitConverter.GetBytes(Convert.ToDouble(value));
                            if (BitConverter.IsLittleEndian) Array.Reverse(number);
                            writer.Write(number); break;
                        default: Be(writer, Convert.ToInt64(value), Size(type)); break;
                    }
                }
                foreach (var column in columns) {
                    writer.Write((byte)(column.Storage | column.Type)); Be(writer, AddString(column.Name), 4);
                    if (column.Storage == 48) WriteValue(column.Type, column.Constant);
                }
                int rowsAt = checked((int)output.Position), width = 0;
                foreach (var column in columns.Where(c => c.Storage == 80)) width += Size(column.Type);
                foreach (var row in rows)
                    foreach (var column in columns.Where(c => c.Storage == 80)) WriteValue(column.Type, row[column.Name]);
                int stringOffset = checked((int)output.Position);
                writer.Write(strings.ToArray()); if (top && blobs.Length > 0) Align(writer, 32);
                int blobOffset = checked((int)output.Position);
                writer.Write(blobs.ToArray()); Align(writer, top ? 32 : 4);
                output.Position = 0; writer.Write(Encoding.ASCII.GetBytes("@UTF"));
                if (rowsAt - 8 > 65535 || width > 65535) throw new InvalidDataException("CRI UTF table exceeds native field limits.");
                Be(writer, output.Length - 8, 4); writer.Write(original, 8, 2); Be(writer, rowsAt - 8, 2); Be(writer, stringOffset - 8, 4);
                Be(writer, blobOffset - 8, 4); Be(writer, tableName, 4); Be(writer, columns.Count, 2);
                Be(writer, width, 2); Be(writer, rows.Count, 4);
                return output.ToArray();
            }
        }
        static int Size(int type) => type < 2 ? 1 : type < 4 ? 2 : type == 6 || type == 7 || type == 9 || type == 11 ? 8 : 4;
        internal static void Be(BinaryWriter writer, long value, int bytes) { for (int i = bytes - 1; i >= 0; i--) writer.Write((byte)(value >> (i * 8))); }
        static void Align(BinaryWriter writer, int alignment) { while (writer.BaseStream.Position % alignment != 0) writer.Write((byte)0); }
    }
}
