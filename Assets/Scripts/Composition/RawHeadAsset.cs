using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using G4Runtime;

namespace G4Composition
{
    public sealed class RawHeadAsset
    {
        public string RelativePath, ModelPath, TexturePath, TextureError;
        public byte[] Metadata, Geometry, TextureContainer;
        public G4ModelParser.Model Preview;
        public readonly List<RawMesh> Meshes = new List<RawMesh>();
        public readonly List<byte[]> Layouts = new List<byte[]>();
        public readonly List<RawTexture> Textures = new List<RawTexture>();
        public UnityEngine.Rect LegacyHairAtlasBounds = new UnityEngine.Rect(0, 0, 1, 1);
        public G4Skeleton Skeleton;
        public string SkeletonError;
        internal G4ModelParser.Binary Reader => new G4ModelParser.Binary(Metadata, ModelPath);
        public int Section(int field) => checked(Reader.U16(10) * 4 + Reader.U16(field) * 4);
        public byte[] SectionBytes(int field, int length)
        {
            int start = Section(field); Reader.Range(start, length); return Metadata.Skip(start).Take(length).ToArray();
        }
        public static RawHeadAsset Read(string common, string dx11, string relative, CancellationToken token)
        {
            string face = Path.Combine(common, "chr", "_face"), path = Path.GetFullPath(Path.Combine(face, relative));
            if (!path.StartsWith(face.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Model path outside head directory.");
            var asset = new RawHeadAsset { RelativePath = relative, ModelPath = path, Metadata = File.ReadAllBytes(path), Geometry = File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")) };
            asset.Preview = G4ModelParser.Parse(asset.Metadata, asset.Geometry, path, token);
            var b = asset.Reader; int table = b.U16(4), count = b.U16(0x20), cursor = table + count * 0x50;
            for (int i = 0; i < b.U8(0x26); i++)
            {
                int size = 8 + b.U8(cursor + 1) * 8; b.Range(cursor, size); asset.Layouts.Add(asset.Metadata.Skip(cursor).Take(size).ToArray()); cursor += size;
            }
            for (int i = 0; i < count; i++)
            {
                int o = table + i * 0x50, stride = b.U16(o + 0x3e) & 255;
                if (stride == 0) stride = 0x44;
                int vertexCount = b.Int(o + 8), indexCount = b.Int(o + 12);
                var mg = new G4ModelParser.Binary(asset.Geometry, path + " geometry");
                mg.Range(b.Int(o), vertexCount * (long)stride); mg.Range(b.Int(0x5c) + b.Int(o + 4), indexCount * 2L);
                int paletteCount = (b.U16(o + 0x3c) & 0x100) != 0 ? b.U8(o + 0x3c) : 0;
                int paletteStart = b.U16(o + 0x3a);
                var mesh = new RawMesh { Index = i, Record = asset.Metadata.Skip(o).Take(0x50).ToArray(), Stride = stride, VertexCount = vertexCount, Vertices = asset.Geometry.Skip(b.Int(o)).Take(checked(vertexCount * stride)).ToArray(), Indices = new ushort[indexCount], Palette = new uint[paletteCount], Layout = b.U8(o + 0x42), Material = b.U8(o + 0x43) };
                for (int j = 0; j < indexCount; j++) { mesh.Indices[j] = mg.U16(b.Int(0x5c) + b.Int(o + 4) + j * 2); if (mesh.Indices[j] >= vertexCount) throw new InvalidDataException(path + ": invalid index."); }
                for (int j = 0; j < paletteCount; j++)
                {
                    int joint = b.U16(asset.Section(0x82) + (paletteStart + j) * 2);
                    if (joint >= b.U16(0x24)) throw new InvalidDataException(path + ": invalid bone palette.");
                    mesh.Palette[j] = b.U32(asset.Section(0x74) + joint * 4);
                }
                asset.Meshes.Add(mesh);
            }
            string textureRoot = Path.Combine(dx11, "chr", "_face"), direct = Path.ChangeExtension(Path.Combine(textureRoot, relative), ".g4tx");
            if (File.Exists(direct)) asset.TexturePath = direct;
            else if (File.Exists(Path.ChangeExtension(Path.GetDirectoryName(direct), ".g4tx"))) asset.TexturePath = Path.ChangeExtension(Path.GetDirectoryName(direct), ".g4tx");
            else
            {
                var candidates = Directory.EnumerateFiles(textureRoot, Path.GetFileName(direct), SearchOption.AllDirectories).Take(2).ToArray();
                if (candidates.Length == 1) asset.TexturePath = candidates[0];
            }
            if (asset.TexturePath != null)
            {
                try
                {
                    asset.TextureContainer = File.ReadAllBytes(asset.TexturePath);
                    asset.Textures.AddRange(RawTexture.Read(asset.TextureContainer, asset.TexturePath));
                }
                catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException || e is OverflowException)
                {
                    asset.TextureError = e.Message; asset.Preview.Warnings.Add(e.Message);
                }
            }
            token.ThrowIfCancellationRequested();
            return asset;
        }
        public static string Digest(byte[] bytes) { using (var sha = SHA256.Create()) return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", "").ToLowerInvariant(); }
    }
    public sealed class RawMesh
    {
        public int Index, Stride, VertexCount, Layout, Material;
        public bool PreserveSkinning;
        public byte[] Record, Vertices;
        public ushort[] Indices;
        public uint[] Palette;
    }
    public sealed class RawTexture
    {
        public string Name;
        public uint Hash;
        public byte[] Descriptor, Dds;
        public static List<RawTexture> Read(byte[] data, string path)
        {
            var b = new G4ModelParser.Binary(data, path); b.Magic("G4TX");
            int header = b.U16(4), count = b.U16(0x20), total = b.U16(0x22), sub = b.U8(0x25);
            if (header != 0x60 || count != total || sub != 0 || count > 255) throw new InvalidDataException(path + ": unsupported G4TX table structure.");
            int hash = (header + count * 48 + 15) & ~15;
            int names = (hash + total * 5 + 3) & ~3;
            int payload = checked((header + b.Int(12) + 15) & ~15);
            var result = new List<RawTexture>();
            for (int i = 0; i < count; i++)
            {
                int e = header + i * 48, offset = checked(payload + b.Int(e + 4)), size = b.Int(e + 8);
                b.Range(e, 48); b.Range(offset, size);
                string name = b.Text(names + b.U16(names + i * 2));
                var texture = new RawTexture { Name = name, Hash = b.U32(hash + i * 4), Descriptor = data.Skip(e).Take(48).ToArray(), Dds = data.Skip(offset).Take(size).ToArray() };
                if (texture.Hash != G4TextureParser.Crc32(name)) throw new InvalidDataException(path + ": texture hash/name mismatch: " + name);
                new G4ModelParser.Binary(texture.Dds, path + ": " + name).Magic("DDS ");
                result.Add(texture);
            }
            return result;
        }
        public static byte[] SortLookupIndices(byte[] container)
        {
            var textures = Read(container, "G4TX lookup validation");
            int hashes = (BitConverter.ToUInt16(container, 4) + textures.Count * 48 + 15) & ~15;
            int indices = hashes + textures.Count * 4;
            byte[] expected = Enumerable.Range(0, textures.Count).OrderBy(i => textures[i].Hash).Select(i => checked((byte)i)).ToArray();
            if (expected.SequenceEqual(container.Skip(indices).Take(expected.Length))) return container;
            byte[] output = (byte[])container.Clone(); Buffer.BlockCopy(expected, 0, output, indices, expected.Length);
            return output;
        }
        public static byte[] Write(byte[] templateHeader, IReadOnlyList<RawTexture> textures)
        {
            if (textures.Count == 0 || textures.Count > 255) throw new InvalidDataException("G4TX supports 1–255 textures in this writer.");
            using (var ms = new MemoryStream()) using (var w = new BinaryWriter(ms))
            {
                w.Write(templateHeader.Take(0x60).ToArray()); w.Write(new byte[textures.Count * 48]);
                Pad(w, 16); foreach (var t in textures) w.Write(G4TextureParser.Crc32(t.Name));
                foreach (int i in Enumerable.Range(0, textures.Count).OrderBy(i => G4TextureParser.Crc32(textures[i].Name))) w.Write(checked((byte)i));
                Pad(w, 4); int names = (int)ms.Position; w.Write(new byte[textures.Count * 2]);
                Pad(w, 4);
                for (int i = 0; i < textures.Count; i++)
                {
                    long p = ms.Position; ms.Position = names + i * 2; w.Write(checked((ushort)(p - names))); ms.Position = p;
                    w.Write(System.Text.Encoding.ASCII.GetBytes(textures[i].Name)); w.Write((byte)0);
                }
                Pad(w, 4); int tableSize = (int)ms.Position - 0x60; Pad(w, 16); int payload = (int)ms.Position;
                for (int i = 0; i < textures.Count; i++)
                {
                    var t = textures[i]; int p = (int)ms.Position; w.Write(t.Dds); Pad(w, 16); long end = ms.Position;
                    ms.Position = 0x60 + i * 48; w.Write(t.Descriptor); ms.Position = 0x64 + i * 48; w.Write(p - payload); w.Write(t.Dds.Length); ms.Position = end;
                }
                int size = (int)ms.Length; ms.Position = 12; w.Write(tableSize); ms.Position = 0x20; w.Write((ushort)textures.Count); w.Write((ushort)textures.Count); ms.Position = 0x25; w.Write((byte)0); ms.Position = 0x2c; w.Write(size - payload);
                var output = ms.ToArray(); var check = Read(output, "G4TX output");
                for (int i = 0; i < textures.Count; i++) if (!check[i].Dds.SequenceEqual(textures[i].Dds)) throw new InvalidDataException("DDS payload changed during export.");
                return output;
            }
        }
        internal static void Pad(BinaryWriter w, int n) { while (w.BaseStream.Position % n != 0) w.Write((byte)0); }
    }
}

