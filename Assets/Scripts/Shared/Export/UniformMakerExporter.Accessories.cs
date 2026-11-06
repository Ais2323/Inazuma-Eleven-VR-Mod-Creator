using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;

public static partial class UniformMakerExporter
{
    static void Put(byte[] bytes, int offset, int value) => BitConverter.GetBytes(value).CopyTo(bytes, offset);
    // Explicit, invisible references prevent a missing body-specific reference
    // from inheriting the first body's visible skin/number/accessory model.
    static string[] BuildEmptyResource(string mesh, string texture, UniformPartKind kind, Dictionary<string, byte[]> pending)
    {
        string folder = "_uniform/empty_" + Digest(kind + "|" + mesh + "|" + texture);
        string modelRelative = folder + "/" + Path.GetFileName(mesh), textureRelative = folder + "/" + Path.GetFileName(texture);
        string mdOutput = "common/chr/" + modelRelative;
        if (pending.ContainsKey(mdOutput)) return new[] { modelRelative, textureRelative };
        byte[] md = File.ReadAllBytes(Path.Combine(DumpDataManager.CommonPath, "chr", mesh));
        byte[] originalGeometry = File.ReadAllBytes(Path.ChangeExtension(Path.Combine(DumpDataManager.CommonPath, "chr", mesh), ".g4mg"));
        var b = new G4ModelParser.Binary(md, mesh); int table = b.U16(4), count = b.U16(0x20), cursor = table + count * 0x50;
        var layouts = new List<byte[]>();
        for (int i = 0; i < b.U8(0x26); i++) { int size = 8 + b.U8(cursor + 1) * 8; layouts.Add(md.Skip(cursor).Take(size).ToArray()); cursor += size; }
        var records = new List<byte[]>();
        byte[] mg;
        using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
            for (int i = 0; i < count; i++) {
                int at = table + i * 0x50, stride = b.U16(at + 0x3e) & 255; if (stride == 0) stride = 0x44;
                var record = md.Skip(at).Take(0x50).ToArray(); bool active = b.Int(at + 8) > 0;
                Put(record, 0, (int)stream.Position); Put(record, 8, active ? 1 : 0); Put(record, 12, active ? 3 : 0); Put(record, 0x30, active ? 1 : 0); Array.Clear(record, 0x10, 24);
                if (active) {
                    var vertex = originalGeometry.Skip(b.Int(at)).Take(stride).ToArray(); var layout = layouts[b.U8(at + 0x42)];
                    for (int j = 0; j < layout[1]; j++) if (layout[8 + j * 8] == 1) Array.Clear(vertex, BitConverter.ToUInt16(layout, 9 + j * 8), 12);
                    writer.Write(vertex); RawTexture.Pad(writer, 16);
                }
                records.Add(record);
            }
            int indexBase = (int)stream.Position;
            foreach (var record in records) { Put(record, 4, (int)stream.Position - indexBase); if (BitConverter.ToInt32(record, 8) != 0) { writer.Write((ushort)0); writer.Write((ushort)0); writer.Write((ushort)0); } }
            Put(md, 0x50, indexBase); Put(md, 0x54, (int)stream.Position - indexBase); Put(md, 0x5c, indexBase); RawTexture.Pad(writer, 16); mg = stream.ToArray();
        }
        for (int i = 0; i < count; i++) Buffer.BlockCopy(records[i], 0, md, table + i * 0x50, 0x50);
        Array.Clear(md, 0x30, 24);
        var check = G4ModelParser.Parse(md, mg, "Invisible uniform accessory");
        if (check.Parts.Any(p => p.Indices.Any(index => index != 0))) throw new InvalidDataException("Invisible accessory contains visible triangles.");
        pending.Add(mdOutput, md); pending.Add(Path.ChangeExtension(mdOutput, ".g4mg"), mg);
        pending.Add("dx11/chr/" + textureRelative, File.ReadAllBytes(Path.Combine(DumpDataManager.Dx11Path, "chr", texture)));
        return new[] { modelRelative, textureRelative };
    }
}
