using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G4Composition;
using G4Runtime;
using UnityEngine;

internal static class UniformFbxWriter
{
    internal sealed class Result { internal byte[] Metadata, Geometry; }
    internal static string[] Slots(byte[] metadata)
    {
        var b = new G4ModelParser.Binary(metadata, "Uniform FBX template"); int bias = b.U16(10) * 4, names = bias + b.U16(0x84) * 4;
        return Enumerable.Range(0, b.U16(0x20)).Select(i => b.Text(names + b.U16(names + i * 2))).ToArray();
    }
    static string BaseName(string name) => Regex.Replace(name, @"_LOD\d*$", "", RegexOptions.IgnoreCase);
    static void Put(byte[] bytes, int at, int value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
    static void Put(byte[] bytes, int at, ushort value) => BitConverter.GetBytes(value).CopyTo(bytes, at);
    static void Bounds(byte[] bytes, int at, UnityEngine.Bounds bounds) { var p = bounds.min; var q = bounds.max; foreach (float value in new[] { p.x, p.y, -q.z, q.x, q.y, -p.z }) { BitConverter.GetBytes(value).CopyTo(bytes, at); at += 4; } }

    internal static Result Build(byte[] metadata, byte[] geometry, UniformFbxModel settings, G4Skeleton rig)
    {
        settings.Validate(); var source = UniformFbxReader.Read(settings);
        var b = new G4ModelParser.Binary(metadata, "Uniform FBX template"); int count = b.U16(0x20), table = b.U16(4), cursor = table + count * 0x50;
        var names = Slots(metadata); var preview = G4ModelParser.Parse(metadata, geometry, "Uniform FBX template");
        if (settings.meshSlots.Any(slot => slot >= count || !preview.Parts.Any(p => p.SourceMeshIndex == slot))) throw new InvalidDataException("FBX target mesh slot does not exist in this uniform.");
        var layouts = new List<byte[]>();
        for (int i = 0; i < b.U8(0x26); i++) { int size = 8 + b.U8(cursor + 1) * 8; layouts.Add(metadata.Skip(cursor).Take(size).ToArray()); cursor += size; }
        var meshes = new List<RawMesh>(); var palette = new List<ushort>(); UnityEngine.Bounds overall = default; bool haveBounds = false;
        var owner = new GameObject("FBX encoding buffers"); owner.hideFlags = HideFlags.HideAndDontSave;
        try {
            for (int i = 0; i < count; i++) {
                int record = table + i * 0x50, stride = b.U16(record + 0x3e) & 255;
                if (stride == 0) stride = 0x44;
                var raw = new RawMesh { Record = metadata.Skip(record).Take(0x50).ToArray(), Stride = stride, Layout = b.U8(record + 0x42), Vertices = geometry.Skip(b.Int(record)).Take(stride).ToArray() };
                int slot = preview.Parts.FirstOrDefault(p => BaseName(p.Name) == BaseName(names[i]))?.SourceMeshIndex ?? i;
                source.BindMesh(settings, rig, slot, out var mesh, out var hashes, out var weights);
                try {
                    var skin = owner.AddComponent<AvatarMeshSkin>(); skin.Hashes = hashes; skin.Weights = weights;
                    AvatarCompositionAsset.EncodeMesh(raw, layouts[raw.Layout], mesh, Matrix4x4.identity, rig.Hashes[0], skin);
                    Put(raw.Record, 0x3a, checked((ushort)palette.Count)); Put(raw.Record, 0x3c, checked((ushort)(0x100 | raw.Palette.Length))); Put(raw.Record, 0x38, (ushort)0);
                    palette.AddRange(raw.Palette.Select(hash => checked((ushort)rig.ByHash[hash])));
                    Bounds(raw.Record, 0x10, mesh.bounds);
                    if (mesh.vertexCount > 0) { if (!haveBounds) { overall = mesh.bounds; haveBounds = true; } else overall.Encapsulate(mesh.bounds); }
                    meshes.Add(raw);
                } finally { if (Application.isPlaying) UnityEngine.Object.Destroy(mesh); else UnityEngine.Object.DestroyImmediate(mesh); }
            }
            if (!haveBounds) throw new InvalidDataException("FBX has no bound geometry.");
            byte[] md = (byte[])metadata.Clone();
            byte[] PackUInt(IEnumerable<uint> values) { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); foreach (uint v in values) writer.Write(v); return stream.ToArray(); }
            byte[] PackShort(IEnumerable<ushort> values) { using var stream = new MemoryStream(); using var writer = new BinaryWriter(stream); foreach (ushort v in values) writer.Write(v); return stream.ToArray(); }
            md = G4CompositionWriter.ReplaceNativeSection(md, 0x74, PackUInt(rig.Hashes));
            md = G4CompositionWriter.ReplaceNativeSection(md, 0x82, PackShort(palette)); Put(md, 0x24, checked((ushort)rig.Hashes.Length));
            byte[] mg;
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream)) {
                foreach (var raw in meshes) { Put(raw.Record, 0, (int)stream.Position); writer.Write(raw.Vertices); RawTexture.Pad(writer, 16); Put(raw.Record, 8, raw.VertexCount); }
                int vertexSize = (int)stream.Position;
                foreach (var raw in meshes) { Put(raw.Record, 4, (int)stream.Position - vertexSize); Put(raw.Record, 12, raw.Indices.Length); Put(raw.Record, 0x30, raw.Indices.Length / 3); foreach (ushort index in raw.Indices) writer.Write(index); }
                Put(md, 0x50, vertexSize); Put(md, 0x54, (int)stream.Position - vertexSize); Put(md, 0x5c, vertexSize); RawTexture.Pad(writer, 16); mg = stream.ToArray();
            }
            for (int i = 0; i < count; i++) Buffer.BlockCopy(meshes[i].Record, 0, md, table + i * 0x50, 0x50);
            Bounds(md, 0x30, overall); var check = G4ModelParser.Parse(md, mg, "FBX uniform readback");
            if (check.Parts.Sum(p => p.Indices.Length) != source.Parts.Sum(p => p.Positions.Length)) throw new InvalidDataException("FBX triangle readback mismatch.");
            return new Result { Metadata = md, Geometry = mg };
        }
        finally { if (Application.isPlaying) UnityEngine.Object.Destroy(owner); else UnityEngine.Object.DestroyImmediate(owner); }
    }
}
