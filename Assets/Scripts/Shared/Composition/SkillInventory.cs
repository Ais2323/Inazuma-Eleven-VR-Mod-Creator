using System;
using System.Collections.Generic;
using System.IO;
using G4Runtime;

namespace G4Composition
{
    // Read only the verified scalar ID fields; other RDBNP fields remain untouched.
    public static class SkillInventory
    {
        public static Dictionary<uint, uint> Read(byte[] data, string source)
        {
            var b = new G4ModelParser.Binary(data, source); b.Magic("RDBNP");
            int baseline = checked(b.U16(10) * 4); b.Range(baseline, b.Int(12));
            Func<int, int> section = f => checked(baseline + b.U16(f) * 4);
            int types = section(0x24), fields = section(0x28), roots = section(0x2c), hashes = section(0x30), offsets = section(0x32), values = section(0x36), strings = checked(baseline + b.Int(0x38));
            int typeCount = b.U16(0x26), fieldCount = b.U16(0x2a), rootCount = b.U16(0x2e), hashCount = b.U16(0x34);
            b.Range(types, typeCount * 32L); b.Range(fields, fieldCount * 32L); b.Range(roots, rootCount * 32L); b.Range(hashes, hashCount * 4L); b.Range(offsets, hashCount * 4L);
            var names = new Dictionary<uint, string>();
            for (int i = 0; i < hashCount; i++) names[b.U32(hashes + i * 4)] = b.Text(checked(strings + b.Int(offsets + i * 4)));
            for (int r = 0; r < rootCount; r++)
            {
                int root = roots + r * 32;
                if (!names.TryGetValue(b.U32(root + 16), out string list) || list != "m_skillInfoList") continue;
                int type = b.U16(root); if (type >= typeCount) throw new InvalidDataException(source + ": invalid type.");
                int first = b.U16(types + type * 32 + 8), count = b.U16(types + type * 32 + 10);
                if (first + count > fieldCount) throw new InvalidDataException(source + ": invalid fields.");
                int id = -1, name = -1, stride = b.Int(root + 8), rowCount = b.Int(root + 12), rows = checked(values + b.Int(root + 4));
                b.Range(rows, rowCount * (long)stride);
                for (int i = first; i < first + count; i++)
                {
                    int field = fields + i * 32; string n = names[b.U32(field)];
                    if (n != "skillID" && n != "skillNameId") continue;
                    int offset = b.Int(field + 12);
                    if (b.U16(field + 4) != 15 || b.Int(field + 8) != 4 || b.Int(field + 16) != 1 || offset > stride - 4) throw new InvalidDataException(source + ": unsupported skill ID field.");
                    if (n == "skillID") id = offset; else name = offset;
                }
                if (id < 0 || name < 0) throw new InvalidDataException(source + ": missing skill identifiers.");
                var result = new Dictionary<uint, uint>();
                for (int i = 0; i < rowCount; i++) result.Add(b.U32(rows + i * stride + id), b.U32(rows + i * stride + name));
                return result;
            }
            throw new InvalidDataException(source + ": missing m_skillInfoList.");
        }
    }
}

