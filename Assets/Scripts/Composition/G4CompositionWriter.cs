using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using G4Runtime;
using UnityEngine;

namespace G4Composition
{
    public sealed class G4CompositionWriter
    {
        public sealed class Result { public byte[] Metadata, Geometry, Textures; public List<string> Warnings = new List<string>(); }
        sealed class OutputMesh { public byte[] Record, Vertices, Layout; public ushort[] Indices; public uint[] Palette; public string Name; public Bounds Bounds; public int Material; public bool PreserveSkinning; }
        sealed class OutputMaterial { public byte[] Record, Colors, Parameters, States, Uv, UvExtra; public string Name; public uint Shader; public List<byte[]> Refs = new List<byte[]>(); public List<byte[]> Samplers = new List<byte[]>(); public List<uint> UvHashes = new List<uint>(); }
        static ushort U16(byte[] b, int o) => BitConverter.ToUInt16(b, o);
        static void Put(byte[] b, int o, ushort v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 2);
        static void Put(byte[] b, int o, int v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 4);
        static void Put(byte[] b, int o, float v) => Buffer.BlockCopy(BitConverter.GetBytes(v), 0, b, o, 4);
        static byte[] Slice(byte[] data, int offset, int size) { new G4ModelParser.Binary(data, "asset").Range(offset, size); return data.Skip(offset).Take(size).ToArray(); }
        static byte[] Pack(IEnumerable<uint> values) { using (var s = new MemoryStream()) using (var w = new BinaryWriter(s)) { foreach (uint v in values) w.Write(v); return s.ToArray(); } }
        static byte[] Shorts(IEnumerable<int> values) { using (var s = new MemoryStream()) using (var w = new BinaryWriter(s)) { foreach (int v in values) w.Write(checked((ushort)v)); return s.ToArray(); } }
        static byte[] SortedIds(IEnumerable<uint> hashes) => Shorts(hashes.Select((hash, index) => new { hash, index }).OrderBy(v => v.hash).Select(v => v.index));
        static byte[] Names(IEnumerable<string> names)
        {
            string[] list = names.ToArray();
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            {
                w.Write(new byte[list.Length * 2]);
                for (int i = 0; i < list.Length; i++) { long p = s.Position; s.Position = i * 2; w.Write(checked((ushort)p)); s.Position = p; w.Write(Encoding.ASCII.GetBytes(list[i])); w.Write((byte)0); }
                return s.ToArray();
            }
        }
        static string StableName(string prefix, string key) => prefix + "_" + RawHeadAsset.Digest(Encoding.UTF8.GetBytes(key)).Substring(0, 16);
        public Result Build(CompositionDocument document, IReadOnlyDictionary<string, RawHeadAsset> sources, RawHeadAsset target, CancellationToken token)
        {
            if (document == null || document.Parts.Count == 0) throw new InvalidDataException("拼貼清單沒有部件。");
            CheckNative(target);
            var meshes = new List<OutputMesh>(); var materials = new List<OutputMaterial>(); var textures = new List<RawTexture>();
            var materialIds = new Dictionary<string, int>(); var textureIds = new Dictionary<string, int>();
            // A resolved G4TX may be a parent container shared with other target models.
            // Retain its entries so replacing this head does not remove their texture dependencies.
            foreach (var texture in target.Textures)
            {
                textureIds.Add(TextureIdentity(texture), textures.Count);
                textures.Add(texture);
            }
            // Use the non-eye/mouth source as the native replacement base. A full
            // hair model replacement is accepted by the game, and its G4MD owns
            // the joint table and palette capacity required by that geometry.
            var nativePart = document.Parts.FirstOrDefault(p =>
                p.Name.IndexOf("eye", StringComparison.OrdinalIgnoreCase) < 0 &&
                p.Name.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) < 0);
            RawHeadAsset nativeBase = nativePart != null && sources.TryGetValue(nativePart.SourceRelativePath, out var hairBase)
                ? hairBase : target;
            if (nativeBase.Meshes.Count != document.Parts.Count)
            {
                nativeBase = document.Parts
                    .Where(p => p.Name.IndexOf("eye", StringComparison.OrdinalIgnoreCase) < 0 &&
                        p.Name.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) < 0)
                    .Select(p => sources.TryGetValue(p.SourceRelativePath, out var source) ? source : null)
                    .Where(source => source != null)
                    .Distinct()
                    .FirstOrDefault(source => source.Meshes.Count == document.Parts.Count)
                    ?? (target.Meshes.Count == document.Parts.Count ? target : nativeBase);
            }
            var joints = Enumerable.Range(0, nativeBase.Reader.U16(0x24))
                .Select(i => nativeBase.Reader.U32(nativeBase.Section(0x74) + i * 4)).ToList();
            uint headJoint = TargetHeadJoint(nativeBase);
            foreach (var part in document.Parts)
            {
                token.ThrowIfCancellationRequested();
                if (!sources.TryGetValue(part.SourceRelativePath, out var source)) throw new InvalidDataException("缺少來源：" + part.SourceRelativePath);
                CheckNative(source);
                if (part.MeshIndex < 0 || part.MeshIndex >= source.Meshes.Count) throw new InvalidDataException(part.Name + ": mesh index out of range.");
                var raw = source.Meshes[part.MeshIndex];
                if (raw.PreserveSkinning)
                {
                    if (target.Skeleton == null) target.Skeleton = new SkeletonResolver(DumpDataManager.CommonPath).Resolve(target, token);
                    foreach (uint hash in raw.Palette)
                    {
                        if (!target.Skeleton.ByHash.ContainsKey(hash))
                            throw new InvalidDataException(part.Name + ": target body skeleton has no Avatar hair joint " + hash.ToString("X8"));
                        if (!joints.Contains(hash)) joints.Add(hash);
                    }
                }
                string materialKey = source.RelativePath + "|" + raw.Material;
                if (!materialIds.TryGetValue(materialKey, out int materialId))
                {
                    materialId = materials.Count; materialIds.Add(materialKey, materialId);
                    var material = Material(source, raw.Material, materialKey, textures, textureIds);
                    if (materials.Any(m => m.Name == material.Name)) material.Name = StableName(material.Name, materialKey);
                    materials.Add(material);
                }
                var mesh = Geometry(source, raw, part, joints, headJoint, token); mesh.Material = materialId;
                mesh.Name = source.Reader.Text(source.Section(0x84) + source.Reader.U16(source.Section(0x84) + part.MeshIndex * 2));
                if (meshes.Any(m => m.Name == mesh.Name)) mesh.Name = StableName(mesh.Name, source.RelativePath + "|" + part.Id);
                uint sourceNode = source.Reader.U32(source.Section(0x74) + BitConverter.ToUInt16(raw.Record, 0x38) * 4);
                uint targetNode = joints.Contains(sourceNode) ? sourceNode : headJoint;
                Put(mesh.Record, 0x38, checked((ushort)joints.IndexOf(targetNode))); meshes.Add(mesh);
            }
            // The retail loader is stricter than the community parsers. Retain
            // the native hair G4MD and G4TX structures, replacing only the face
            // texture payloads in the matching _10 slots.
            var facePart = document.Parts.FirstOrDefault(p =>
                p.Name.IndexOf("eye", StringComparison.OrdinalIgnoreCase) >= 0 ||
                p.Name.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) >= 0);
            RawHeadAsset faceBase = facePart != null && sources.TryGetValue(facePart.SourceRelativePath, out var headBase)
                ? headBase : nativeBase;
            byte[] mergedTextures = MergeFaceTextures(nativeBase, faceBase);
            return BuildConservative(target, nativeBase, meshes, joints, headJoint, mergedTextures, token);
#pragma warning disable CS0162
            if (meshes.Count > ushort.MaxValue || materials.Count > 255 || textures.Count > 255) throw new InvalidDataException("G4MD mesh/material/texture capacity exceeded.");
            var layouts = new List<byte[]>();
            foreach (var mesh in meshes) { int id = layouts.FindIndex(l => l.SequenceEqual(mesh.Layout)); if (id < 0) { id = layouts.Count; layouts.Add(mesh.Layout); } if (id > 255) throw new InvalidDataException("Too many vertex layouts."); mesh.Record[0x42] = (byte)id; mesh.Record[0x43] = (byte)mesh.Material; }
            byte[] geometry;
            int vertexSize, indexSize, indexBase;
            var palette = new List<int>();
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            {
                foreach (var mesh in meshes)
                {
                    Put(mesh.Record, 0, checked((int)s.Position)); w.Write(mesh.Vertices); RawTexture.Pad(w, 16);
                    Put(mesh.Record, 8, mesh.Vertices.Length / (U16(mesh.Record, 0x3e) & 255));
                    Put(mesh.Record, 0x3a, checked((ushort)palette.Count));
                    Put(mesh.Record, 0x3c, (ushort)((U16(mesh.Record, 0x3c) & 0xfe00) | 0x100 | mesh.Palette.Length));
                    palette.AddRange(mesh.Palette.Select(h => joints.IndexOf(h)));
                    PutBounds(mesh.Record, 0x10, mesh.Bounds);
                }
                vertexSize = checked((int)s.Position); indexBase = vertexSize;
                foreach (var mesh in meshes)
                {
                    Put(mesh.Record, 4, checked((int)s.Position - indexBase)); Put(mesh.Record, 12, mesh.Indices.Length); Put(mesh.Record, 0x30, mesh.Indices.Length / 3);
                    foreach (ushort index in mesh.Indices) w.Write(index);
                }
                indexSize = checked((int)s.Position - indexBase); RawTexture.Pad(w, 16); geometry = s.ToArray();
            }
            var refs = new List<byte>(); var samplers = new List<byte>(); var colors = new List<byte>(); var parameters = new List<byte>(); var states = new List<byte>(); var uv = new List<byte>(); var uvExtra = new List<byte>(); var uvHashes = new List<uint>(); var shaders = new List<uint>();
            foreach (var mat in materials)
            {
                Put(mat.Record, 0, checked((ushort)(colors.Count / 48))); colors.AddRange(mat.Colors);
                Put(mat.Record, 2, checked((ushort)(parameters.Count / 16))); parameters.AddRange(mat.Parameters);
                Put(mat.Record, 4, checked((ushort)(states.Count / 2))); states.AddRange(mat.States);
                Put(mat.Record, 6, checked((ushort)(refs.Count / 6)));
                int shader = shaders.IndexOf(mat.Shader); if (shader < 0) { shader = shaders.Count; shaders.Add(mat.Shader); }
                if (shader > 255) throw new InvalidDataException("Too many shaders.");
                Put(mat.Record, 8, (ushort)((U16(mat.Record, 8) & 255) | (shader << 8)));
                Put(mat.Record, 14, checked((ushort)(uv.Count / 32)));
                uv.AddRange(mat.Uv); uvExtra.AddRange(mat.UvExtra);
                for (int i = 0; i < mat.UvHashes.Count; i++)
                {
                    uint hash = mat.UvHashes[i];
                    if (uvHashes.Contains(hash)) hash = G4TextureParser.Crc32(StableName("uv", mat.Name + "|" + i));
                    uvHashes.Add(hash);
                }
                for (int i = 0; i < mat.Refs.Count; i++) { var reference = mat.Refs[i]; Put(reference, 4, checked((ushort)(samplers.Count / 2))); samplers.AddRange(mat.Samplers[i]); refs.AddRange(reference); }
            }
            if (parameters.Count / 16 > 255 || uvHashes.Count > 255) throw new InvalidDataException("G4MD parameter/UV table capacity exceeded.");
            var header = Slice(target.Metadata, 0, 0xa0);
            Put(header, 0x20, checked((ushort)meshes.Count)); Put(header, 0x22, checked((ushort)materials.Count)); Put(header, 0x24, checked((ushort)joints.Count));
            header[0x26] = checked((byte)layouts.Count); header[0x27] = checked((byte)textures.Count); header[0x28] = checked((byte)shaders.Count); header[0x29] = checked((byte)(parameters.Count / 16)); header[0x2a] = checked((byte)uvHashes.Count);
            Put(header, 0x50, vertexSize); Put(header, 0x54, indexSize); Put(header, 0x5c, indexBase);
            Bounds bounds = meshes[0].Bounds; foreach (var mesh in meshes.Skip(1)) bounds.Encapsulate(mesh.Bounds); PutBounds(header, 0x30, bounds);
            var meshHashes = meshes.Select(m => G4TextureParser.Crc32(m.Name)).ToArray(); var matHashes = materials.Select(m => G4TextureParser.Crc32(m.Name)).ToArray();
            if (matHashes.Distinct().Count() != matHashes.Length || uvHashes.Distinct().Count() != uvHashes.Count) throw new InvalidDataException("Material/UV hash collision.");
            byte[] metadata;
            using (var s = new MemoryStream()) using (var w = new BinaryWriter(s))
            {
                w.Write(header); foreach (var mesh in meshes) w.Write(mesh.Record);
                var offsets = new Dictionary<int, int>();
                Action<int, byte[], int> section = (field, bytes, alignment) => { RawTexture.Pad(w, alignment); offsets.Add(field, checked((int)s.Position)); w.Write(bytes); };
                section(0x62, layouts.SelectMany(l => l).ToArray(), 4);
                section(0x64, materials.SelectMany(m => m.Record).ToArray(), 16);
                section(0x66, colors.ToArray(), 4); section(0x68, refs.ToArray(), 4);
                section(0x6a, parameters.ToArray(), 16); section(0x88, uv.ToArray(), 16); section(0x8a, uvExtra.ToArray(), 4);
                section(0x6c, states.ToArray(), 4); section(0x6e, samplers.ToArray(), 4);
                section(0x70, Pack(meshHashes), 4); section(0x72, Pack(matHashes), 4);
                section(0x74, Pack(joints), 4); section(0x76, Pack(textures.Select(t => G4TextureParser.Crc32(t.Name))), 4);
                section(0x78, Pack(shaders), 4); section(0x7a, Pack(uvHashes), 4);
                section(0x7c, SortedIds(meshHashes), 4); section(0x7e, SortedIds(matHashes), 4); section(0x80, SortedIds(uvHashes), 4);
                section(0x82, Shorts(palette), 4); section(0x84, Names(meshes.Select(m => m.Name)), 4); section(0x86, Names(materials.Select(m => m.Name)), 4); RawTexture.Pad(w, 4);
                foreach (var offset in offsets) { s.Position = offset.Key; w.Write(checked((ushort)((offset.Value - 0xa0) / 4))); }
                s.Position = 12; w.Write(checked((int)s.Length - 0xa0)); metadata = s.ToArray();
            }
            var parsed = G4ModelParser.Parse(metadata, geometry, "composition.g4md", token);
            if (parsed.Parts.Count != meshes.Count || parsed.Parts.Sum(m => m.Indices.Length) != meshes.Sum(m => m.Indices.Length)) throw new InvalidDataException("G4MD geometry roundtrip mismatch.");
            byte[] textureHeader = target.TextureContainer ?? sources.Values.First(a => a.TextureContainer != null).TextureContainer;
            return new Result { Metadata = metadata, Geometry = geometry, Textures = RawTexture.Write(textureHeader, textures) };
#pragma warning restore CS0162
        }
        static Result BuildConservative(RawHeadAsset target, RawHeadAsset nativeBase, List<OutputMesh> authored, List<uint> targetJoints, uint headJoint, byte[] mergedTextures, CancellationToken token)
        {
            if (authored.Count != nativeBase.Meshes.Count)
                throw new InvalidDataException("找不到具有 " + authored.Count + " 個 mesh 的原生格式基底（目前基底 " + nativeBase.Meshes.Count + " 個）。");
            Func<string, int> kind = name => name.IndexOf("eye", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : name.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) >= 0 ? 2 : 0;
            var remaining = new List<OutputMesh>(authored); var meshes = new List<OutputMesh>();
            for (int i = 0; i < nativeBase.Meshes.Count; i++)
            {
                string baseName = nativeBase.Reader.Text(nativeBase.Section(0x84) + nativeBase.Reader.U16(nativeBase.Section(0x84) + i * 2));
                int match = remaining.FindIndex(m => kind(m.Name) == kind(baseName));
                if (match < 0) throw new InvalidDataException("找不到可對應原生 mesh：" + baseName);
                var mesh = remaining[match]; remaining.RemoveAt(match);
                // A layout's second word binds its shader, not just vertex formats.
                // Source-local shader indices can otherwise match an unrelated Other
                // shader and leave facial material animation disconnected.
                int materialRecord = nativeBase.Section(0x64) + nativeBase.Meshes[i].Material * 16;
                int shader = nativeBase.Reader.U8(materialRecord + 9);
                int layout = nativeBase.Layouts.FindIndex(l =>
                    BitConverter.ToInt32(l, 4) == shader && l.SequenceEqual(mesh.Layout));
                if (layout < 0)
                {
                    layout = nativeBase.Meshes[i].Layout;
                    TranscodeVertexLayout(mesh, nativeBase.Layouts[layout], nativeBase.Meshes[i].Stride);
                }
                mesh.Layout = nativeBase.Layouts[layout];
                mesh.Record = (byte[])nativeBase.Meshes[i].Record.Clone();
                mesh.Record[0x42] = checked((byte)layout); mesh.Record[0x43] = checked((byte)nativeBase.Meshes[i].Material);
                if (!mesh.PreserveSkinning)
                {
                    RemapLocalPalette(mesh, nativeBase.Meshes[i].Palette, headJoint);
                    mesh.Palette = (uint[])nativeBase.Meshes[i].Palette.Clone();
                }
                uint sourceNode = nativeBase.Reader.U32(nativeBase.Section(0x74) + BitConverter.ToUInt16(mesh.Record, 0x38) * 4);
                Put(mesh.Record, 0x38, checked((ushort)Math.Max(0, targetJoints.IndexOf(targetJoints.Contains(sourceNode) ? sourceNode : headJoint))));
                meshes.Add(mesh);
            }
            byte[] geometry; int vertexSize, indexBase, indexSize;
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                foreach (var mesh in meshes)
                {
                    Put(mesh.Record, 0, checked((int)stream.Position)); writer.Write(mesh.Vertices); RawTexture.Pad(writer, 16);
                    Put(mesh.Record, 8, mesh.Vertices.Length / (U16(mesh.Record, 0x3e) & 255)); PutBounds(mesh.Record, 0x10, mesh.Bounds);
                }
                vertexSize = checked((int)stream.Position); indexBase = vertexSize;
                foreach (var mesh in meshes)
                {
                    Put(mesh.Record, 4, checked((int)stream.Position - indexBase)); Put(mesh.Record, 12, mesh.Indices.Length); Put(mesh.Record, 0x30, mesh.Indices.Length / 3);
                    foreach (ushort index in mesh.Indices) writer.Write(index);
                }
                indexSize = checked((int)stream.Position - indexBase); RawTexture.Pad(writer, 16); geometry = stream.ToArray();
            }
            byte[] metadata = (byte[])nativeBase.Metadata.Clone();
            int jointTable = nativeBase.Section(0x74);
            int writableJoints = Math.Min(targetJoints.Count, nativeBase.Reader.U16(0x24));
            for (int i = 0; i < writableJoints; i++) Put(metadata, jointTable + i * 4, unchecked((int)targetJoints[i]));
            if (meshes.Any(m => m.PreserveSkinning))
            {
                // Keep native physical section order. Appending palettes after the
                // name tables is not accepted by readers which bound these sections
                // using the following table's offset.
                if (targetJoints.Count != nativeBase.Reader.U16(0x24))
                    metadata = ReplaceNativeSection(metadata, 0x74, Pack(targetJoints));
                var palette = new List<int>();
                foreach (var mesh in meshes)
                {
                    if (mesh.Palette.Length > 255) throw new InvalidDataException("Avatar joint palette capacity exceeded.");
                    Put(mesh.Record, 0x3a, checked((ushort)palette.Count));
                    Put(mesh.Record, 0x3c, checked((ushort)(0x100 | mesh.Palette.Length)));
                    foreach (uint hash in mesh.Palette)
                    {
                        int index = targetJoints.IndexOf(hash);
                        if (index < 0) throw new InvalidDataException("Missing output skin joint.");
                        palette.Add(index);
                    }
                }
                metadata = ReplaceNativeSection(metadata, 0x82, Shorts(palette));
                Put(metadata, 0x24, checked((ushort)targetJoints.Count));
            }
            for (int i = 0; i < meshes.Count; i++)
                Buffer.BlockCopy(meshes[i].Record, 0, metadata, nativeBase.Reader.U16(4) + i * 0x50, 0x50);
            Put(metadata, 0x50, vertexSize); Put(metadata, 0x54, indexSize); Put(metadata, 0x5c, indexBase);
            Bounds bounds = meshes[0].Bounds; foreach (var mesh in meshes.Skip(1)) bounds.Encapsulate(mesh.Bounds); PutBounds(metadata, 0x30, bounds);
            var parsed = G4ModelParser.Parse(metadata, geometry, "conservative replacement", token);
            if (parsed.Parts.Count != meshes.Count) throw new InvalidDataException("Conservative G4MD readback mismatch.");
            return new Result { Metadata = metadata, Geometry = geometry, Textures = mergedTextures };
        }
        static byte[] ReplaceNativeSection(byte[] metadata, int field, byte[] replacement)
        {
            int bias = U16(metadata, 10) * 4;
            int start = bias + U16(metadata, field) * 4;
            int end = metadata.Length;
            for (int f = 0x62; f <= 0x8a; f += 2)
            {
                if (U16(metadata, f) == 0) continue;
                int offset = bias + U16(metadata, f) * 4;
                if (offset > start) end = Math.Min(end, offset);
            }
            if (start < bias || end <= start) throw new InvalidDataException("Invalid native skin section range.");
            int oldSize = end - start;
            // Retain alignment of every following section, including 16-byte tables.
            int newSize = Math.Max(oldSize, replacement.Length);
            newSize += (16 - (newSize - oldSize) % 16) % 16;
            int delta = newSize - oldSize;
            byte[] output = new byte[checked(metadata.Length + delta)];
            Buffer.BlockCopy(metadata, 0, output, 0, start);
            Buffer.BlockCopy(replacement, 0, output, start, replacement.Length);
            Buffer.BlockCopy(metadata, end, output, end + delta, metadata.Length - end);
            for (int f = 0x62; f <= 0x8a; f += 2)
            {
                if (U16(metadata, f) == 0) continue;
                int offset = bias + U16(metadata, f) * 4;
                if (offset >= end) Put(output, f, checked((ushort)((offset + delta - bias) / 4)));
            }
            Put(output, 12, output.Length - bias);
            return output;
        }

        static byte[] MergeFaceTextures(RawHeadAsset hair, RawHeadAsset head)
        {
            if (hair.TextureContainer == null) throw new InvalidDataException(hair.RelativePath + ": 找不到頭髮 G4TX。");
            if (head.TextureContainer == null) throw new InvalidDataException(head.RelativePath + ": 找不到頭部 G4TX。");
            if (ReferenceEquals(hair, head) || hair.RelativePath.Equals(head.RelativePath, StringComparison.OrdinalIgnoreCase))
                return (byte[])hair.TextureContainer.Clone();

            byte[] output = (byte[])hair.TextureContainer.Clone();
            var headHashes = FaceTextureHashes(head);
            var hairHashes = FaceTextureHashes(hair);
            var headByRole = head.Textures
                .Where(texture => headHashes.Contains(texture.Hash))
                .Select(texture => new { Texture = texture, Role = FaceTextureRole(texture.Name) })
                .Where(item => item.Role != null)
                .GroupBy(item => item.Role, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.Select(item => item.Texture).Single(), StringComparer.OrdinalIgnoreCase);
            int payload = checked((0x60 + BitConverter.ToInt32(output, 12) + 15) & ~15);
            int replaced = 0;
            for (int i = 0; i < hair.Textures.Count; i++)
            {
                if (!hairHashes.Contains(hair.Textures[i].Hash)) continue;
                string role = FaceTextureRole(hair.Textures[i].Name);
                if (role == null || !headByRole.TryGetValue(role, out var source)) continue;
                RawTexture destination = hair.Textures[i];
                if (destination.Dds.Length < 0x94 || source.Dds.Length < 0x94)
                    throw new InvalidDataException(role + ": G4TX DDS object 太小。");
                int destinationPayload = destination.Dds.Length - 0x94;
                int sourcePayload = source.Dds.Length - 0x94;
                if (destinationPayload != sourcePayload ||
                    !destination.Dds.Skip(0x0c).Take(8).SequenceEqual(source.Dds.Skip(0x0c).Take(8)))
                    throw new InvalidDataException(role + ": 頭部與頭髮的貼圖尺寸或 payload 容量不同。");

                int descriptor = 0x60 + i * 48;
                int objectOffset = checked(payload + BitConverter.ToInt32(output, descriptor + 4));
                int objectSize = BitConverter.ToInt32(output, descriptor + 8);
                if (objectSize != destination.Dds.Length || objectOffset < 0 || objectOffset > output.Length - objectSize)
                    throw new InvalidDataException(role + ": 頭髮 G4TX offset 無效。");
                Buffer.BlockCopy(source.Dds, 0x94, output, objectOffset + 0x94, sourcePayload);
                Buffer.BlockCopy(source.Dds, 0x14, output, objectOffset + 0x14, 4);
                Buffer.BlockCopy(source.Dds, 0x1c, output, objectOffset + 0x1c, 4);
                replaced++;
            }
            if (replaced == 0) throw new InvalidDataException("頭部與頭髮 G4TX 找不到可對應的 _10 貼圖槽位。");
            return output;
        }
        static HashSet<uint> FaceTextureHashes(RawHeadAsset asset)
        {
            var hashes = new HashSet<uint>();
            foreach (var part in asset.Preview.Parts)
            {
                if (part.Name.IndexOf("eye", StringComparison.OrdinalIgnoreCase) < 0 &&
                    part.Name.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) < 0) continue;
                foreach (uint hash in asset.Preview.Materials[part.Material].TextureHashes) hashes.Add(hash);
            }
            return hashes;
        }

        static string FaceTextureRole(string name)
        {
            string value = name.ToLowerInvariant();
            foreach (string role in new[] { "10line", "10oc", "10spm", "10sp", "10" })
                if (value.EndsWith("_" + role, StringComparison.Ordinal)) return role;
            return null;
        }

        static void TranscodeVertexLayout(OutputMesh mesh, byte[] targetLayout, int targetStride)
        {
            int sourceStride = U16(mesh.Record, 0x3e) & 255;
            var source = LayoutElements(mesh.Layout, sourceStride);
            var target = LayoutElements(targetLayout, targetStride);
            foreach (int semantic in new[] { 1, 5, 6 })
            {
                if (!source.TryGetValue(semantic, out var from) || !target.TryGetValue(semantic, out var to) || from.Item2 != to.Item2)
                    throw new InvalidDataException(mesh.Name + " 的必要 vertex 欄位 " + semantic + " 無法轉換成目標格式。");
            }

            int count = mesh.Vertices.Length / sourceStride;
            var vertices = new byte[checked(count * targetStride)];
            for (int vertex = 0; vertex < count; vertex++)
            {
                int targetBase = vertex * targetStride;
                if (target.TryGetValue(8, out var color) && color.Item3 >= 4)
                    for (int j = 0; j < 4; j++) vertices[targetBase + color.Item1 + j] = 255;

                foreach (var element in target)
                {
                    if (!source.TryGetValue(element.Key, out var from)) continue;
                    if (from.Item2 == element.Value.Item2)
                    {
                        int length = Math.Min(from.Item3, element.Value.Item3);
                        Buffer.BlockCopy(mesh.Vertices, vertex * sourceStride + from.Item1,
                            vertices, targetBase + element.Value.Item1, length);
                    }
                    else if (element.Key is >= 10 and <= 12 || element.Key == 14)
                    {
                        ConvertNormalizedUv(
                            mesh.Vertices, vertex * sourceStride + from.Item1, from.Item2,
                            vertices, targetBase + element.Value.Item1, element.Value.Item2);
                    }
                }
            }
            mesh.Vertices = vertices;
            mesh.Layout = targetLayout;
            Put(mesh.Record, 0x3e, (ushort)((U16(mesh.Record, 0x3e) & 0xff00) | targetStride));
        }
        static void ConvertNormalizedUv(byte[] source, int sourceOffset, int sourceFormat,
            byte[] target, int targetOffset, int targetFormat)
        {
            bool sourceUnsigned = sourceFormat == 14;
            bool sourceSigned = sourceFormat == 18 || sourceFormat == 20;
            bool targetUnsigned = targetFormat == 14;
            bool targetSigned = targetFormat == 18 || targetFormat == 20;
            if ((!sourceUnsigned && !sourceSigned) || (!targetUnsigned && !targetSigned)) return;

            for (int component = 0; component < 2; component++)
            {
                int from = sourceOffset + component * 2;
                float value = sourceUnsigned
                    ? U16(source, from) / 65535f
                    : BitConverter.ToInt16(source, from) / 32767f;
                int to = targetOffset + component * 2;
                if (targetUnsigned)
                    Put(target, to, (ushort)Mathf.RoundToInt(Mathf.Clamp01(value) * 65535f));
                else
                    Put(target, to, unchecked((ushort)(short)Mathf.RoundToInt(
                        Mathf.Clamp(value, -1f, 1f) * 32767f)));
            }
        }
        static Dictionary<int, Tuple<int, int, int>> LayoutElements(byte[] layout, int stride)
        {
            var entries = new List<Tuple<int, int, int>>();
            for (int i = 0; i < layout[1]; i++)
            {
                int offset = 8 + i * 8;
                entries.Add(Tuple.Create((int)layout[offset], (int)U16(layout, offset + 1), BitConverter.ToInt32(layout, offset + 4)));
            }
            entries.Sort((a, b) => a.Item2.CompareTo(b.Item2));
            var result = new Dictionary<int, Tuple<int, int, int>>();
            for (int i = 0; i < entries.Count; i++)
            {
                int end = stride;
                for (int j = i + 1; j < entries.Count; j++)
                    if (entries[j].Item2 > entries[i].Item2) { end = entries[j].Item2; break; }
                if (entries[i].Item2 < 0 || entries[i].Item2 >= stride || end > stride)
                    throw new InvalidDataException("無效的 vertex layout。");
                result[entries[i].Item1] = Tuple.Create(entries[i].Item2, entries[i].Item3, end - entries[i].Item2);
            }
            return result;
        }

        static int FindSequence(List<uint> table, uint[] values)
        {
            for (int start = 0; start + values.Length <= table.Count; start++)
            {
                bool same = true; for (int i = 0; i < values.Length; i++) if (table[start + i] != values[i]) { same = false; break; }
                if (same) return start;
            }
            return -1;
        }
        static void RemapLocalPalette(OutputMesh mesh, uint[] required, uint headJoint)
        {
            byte[] layout = mesh.Layout; int weights = -1, bones = -1;
            for (int i = 0; i < layout[1]; i++) { int e = 8 + i * 8; if (layout[e] == 5) weights = U16(layout, e + 1); if (layout[e] == 6) bones = U16(layout, e + 1); }
            int stride = U16(mesh.Record, 0x3e) & 255;
            if (weights < 0 || bones < 0) throw new InvalidDataException(mesh.Name + ": missing skin attributes.");
            for (int v = 0; v < mesh.Vertices.Length / stride; v++) for (int j = 0; j < 8; j++)
            {
                int w = U16(mesh.Vertices, v * stride + weights + j * 2); if (w == 0) continue;
                int old = mesh.Vertices[v * stride + bones + j]; if (old >= mesh.Palette.Length) throw new InvalidDataException(mesh.Name + ": invalid local joint.");
                uint hash = mesh.Palette[old]; int mapped = Array.IndexOf(required, hash); if (mapped < 0) mapped = Array.IndexOf(required, headJoint);
                if (mapped < 0) throw new InvalidDataException(mesh.Name + ": target record has no head joint.");
                mesh.Vertices[v * stride + bones + j] = checked((byte)mapped);
            }
        }
        static void CheckNative(RawHeadAsset a)
        {
            if (!string.IsNullOrEmpty(a.TextureError)) throw new InvalidDataException(a.RelativePath + ": " + a.TextureError);
            var b = a.Reader;
            if (b.U16(4) != 0xa0 || b.U16(6) != 0x68 || b.U16(10) * 4 != 0xa0) throw new InvalidDataException(a.RelativePath + ": unsupported metadata version.");
            for (int f = 0x8c; f < 0xa0; f += 2) if (b.U16(f) != 0) throw new InvalidDataException(a.RelativePath + ": unknown active section 0x" + f.ToString("X") + ".");
            for (int i = 0; i < a.Meshes.Count; i++)
            {
                string n = b.Text(a.Section(0x84) + b.U16(a.Section(0x84) + i * 2));
                if (b.U32(a.Section(0x70) + i * 4) != G4TextureParser.Crc32(n)) throw new InvalidDataException(a.RelativePath + ": unsupported mesh hash table.");
            }
        }
        internal static uint TargetHeadJoint(RawHeadAsset target)
        {
            for (int i = 0; i < target.Meshes.Count; i++)
            {
                string name = target.Reader.Text(target.Section(0x84) + target.Reader.U16(target.Section(0x84) + i * 2));
                if (name.IndexOf("mouth", StringComparison.OrdinalIgnoreCase) >= 0 && target.Meshes[i].Palette.Length == 1)
                    return target.Meshes[i].Palette[0];
            }
            var common = target.Meshes.SelectMany(m => m.Palette).GroupBy(h => h).OrderByDescending(g => g.Count()).FirstOrDefault();
            if (common == null) throw new InvalidDataException(target.RelativePath + ": cannot infer target head joint.");
            return common.Key;
        }
        static OutputMaterial Material(RawHeadAsset a, int index, string key, List<RawTexture> textures, Dictionary<string, int> ids)
        {
            var b = a.Reader; int o = a.Section(0x64) + index * 16;
            var m = new OutputMaterial { Record = Slice(a.Metadata, o, 16), Name = a.Preview.Materials[index].Name };
            int n = b.U8(o + 12), uv = b.U16(o + 14);
            m.Colors = Slice(a.Metadata, a.Section(0x66) + b.U16(o) * 48, 48);
            m.Parameters = Slice(a.Metadata, a.Section(0x6a) + b.U16(o + 2) * 16, b.U8(o + 10) * 16);
            m.States = Slice(a.Metadata, a.Section(0x6c) + b.U16(o + 4) * 2, b.U8(o + 11) * 2);
            m.Shader = b.U32(a.Section(0x78) + b.U8(o + 9) * 4);
            m.Uv = Slice(a.Metadata, a.Section(0x88) + uv * 32, n * 32);
            m.UvExtra = Slice(a.Metadata, a.Section(0x8a) + uv * 20, n * 20);
            for (int j = 0; j < n; j++)
            {
                int r = a.Section(0x68) + (b.U16(o + 6) + j) * 6;
                var reference = Slice(a.Metadata, r, 6);
                if (reference[0] >= b.U8(0x27)) throw new InvalidDataException(a.RelativePath + ": texture slot out of range.");
                uint hash = b.U32(a.Section(0x76) + reference[0] * 4);
                var matches = a.Textures.Where(t => t.Hash == hash).ToArray();
                if (matches.Length != 1) throw new InvalidDataException(a.RelativePath + ": missing/ambiguous required texture " + hash.ToString("X8"));
                var t = matches[0];
                var decoded = G4TextureParser.ParseDDS(t.Dds, a.TexturePath + ": " + t.Name);
                if (decoded.Width != U16(t.Descriptor, 24) || decoded.Height != U16(t.Descriptor, 26)) throw new InvalidDataException(t.Name + ": G4TX/DDS dimensions disagree.");
                string identity = TextureIdentity(t);
                if (!ids.TryGetValue(identity, out int texture))
                {
                    texture = textures.Count; ids.Add(identity, texture);
                    // Retain the semantic suffix used by both game assets and the preview texture selector.
                    string name = textures.Any(x => x.Name == t.Name) ? StableName("tex", identity) + "_" + t.Name : t.Name;
                    if (textures.Any(x => G4TextureParser.Crc32(x.Name) == G4TextureParser.Crc32(name))) throw new InvalidDataException("Texture hash collision.");
                    textures.Add(new RawTexture { Name = name, Hash = G4TextureParser.Crc32(name), Descriptor = t.Descriptor, Dds = t.Dds });
                }
                reference[0] = checked((byte)texture); m.Refs.Add(reference);
                m.Samplers.Add(Slice(a.Metadata, a.Section(0x6e) + b.U16(r + 4) * 2, 6));
                m.UvHashes.Add(b.U32(a.Section(0x7a) + (uv + j) * 4));
            }
            return m;
        }
        static string TextureIdentity(RawTexture texture)
        {
            byte[] descriptor = (byte[])texture.Descriptor.Clone(); Array.Clear(descriptor, 4, 8);
            return texture.Name + "|" + RawHeadAsset.Digest(texture.Dds) + "|" + RawHeadAsset.Digest(descriptor);
        }
        static OutputMesh Geometry(RawHeadAsset a, RawMesh raw, CompositionPart part, IReadOnlyList<uint> targetJoints, uint headJoint, CancellationToken token)
        {
            if (part.Triangles == null || part.Triangles.Length == 0) throw new InvalidDataException(part.Name + ": no triangles.");
            var triangles = part.Triangles.Distinct().OrderBy(t => t).ToArray();
            var used = new List<int>(); var remap = new Dictionary<int, ushort>(); var indices = new List<ushort>();
            foreach (int t in triangles)
            {
                if (t < 0 || t >= raw.Indices.Length / 3) throw new InvalidDataException(part.Name + ": selected triangle out of range.");
                for (int j = 0; j < 3; j++) { int v = raw.Indices[t * 3 + j]; if (!remap.TryGetValue(v, out ushort id)) { id = checked((ushort)used.Count); remap.Add(v, id); used.Add(v); } indices.Add(id); }
            }
            byte[] layout = a.Layouts[raw.Layout]; var elements = new Dictionary<int, Tuple<int, int>>();
            for (int i = 0; i < layout[1]; i++) { int e = 8 + i * 8; elements[layout[e]] = Tuple.Create((int)U16(layout, e + 1), BitConverter.ToInt32(layout, e + 4)); }
            if (!elements.TryGetValue(1, out var pos) || pos.Item2 != 3) throw new InvalidDataException(part.Name + ": unsupported position format.");
            if (!elements.TryGetValue(5, out var weights) || weights.Item2 != 32 || !elements.TryGetValue(6, out var boneIds) || boneIds.Item2 != 24) throw new InvalidDataException(part.Name + ": unsupported skin layout.");
            if (weights.Item1 + 16 > raw.Stride || boneIds.Item1 + 8 > raw.Stride) throw new InvalidDataException(part.Name + ": skin layout overrun.");
            var jointMap = raw.Palette.ToDictionary(hash => hash, hash => targetJoints.Contains(hash) ? hash : headJoint);
            uint[] palette = raw.Palette.Select(hash => jointMap[hash]).Distinct().ToArray();
            if (palette.Length > 255) throw new InvalidDataException("Joint palette capacity exceeded.");
            var output = new byte[checked(used.Count * raw.Stride)];
            Matrix4x4 reflection = Matrix4x4.Scale(new Vector3(1, 1, -1)), transform = reflection * part.Matrix * reflection;
            Bounds bounds = default;
            for (int i = 0; i < used.Count; i++)
            {
                if ((i & 1023) == 0) token.ThrowIfCancellationRequested();
                int from = used[i] * raw.Stride, dest = i * raw.Stride; Buffer.BlockCopy(raw.Vertices, from, output, dest, raw.Stride);
                var influences = new Dictionary<uint, int>(); int sum = 0;
                for (int j = 0; j < 8; j++)
                {
                    int weight = U16(output, dest + weights.Item1 + j * 2); if (weight == 0) continue;
                    int local = output[dest + boneIds.Item1 + j]; if (local >= raw.Palette.Length) throw new InvalidDataException(part.Name + ": invalid weight bone index.");
                    uint mapped = jointMap[raw.Palette[local]]; sum += weight;
                    influences[mapped] = influences.TryGetValue(mapped, out int old) ? old + weight : weight;
                }
                if (sum == 0) throw new InvalidDataException(part.Name + ": vertex has no bone weights.");
                Matrix4x4 bake = transform;
                if (bake.determinant <= 1e-8f) throw new InvalidDataException(part.Name + ": singular or mirrored bind/part transform.");
                int p = dest + pos.Item1;
                var vertex = bake.MultiplyPoint3x4(new Vector3(BitConverter.ToSingle(output, p), BitConverter.ToSingle(output, p + 4), BitConverter.ToSingle(output, p + 8)));
                Put(output, p, vertex.x); Put(output, p + 4, vertex.y); Put(output, p + 8, vertex.z);
                if (i == 0) bounds = new Bounds(vertex, Vector3.zero); else bounds.Encapsulate(vertex);
                foreach (int semantic in new[] { 2, 3, 13 })
                    if (elements.TryGetValue(semantic, out var e))
                    {
                        if (e.Item2 != 20 || e.Item1 + 8 > raw.Stride) throw new InvalidDataException(part.Name + ": unsupported direction format.");
                        int d = dest + e.Item1; var v = new Vector3(BitConverter.ToInt16(output, d) / 32767f, BitConverter.ToInt16(output, d + 2) / 32767f, BitConverter.ToInt16(output, d + 4) / 32767f);
                        v = (semantic == 2 ? bake.inverse.transpose : bake).MultiplyVector(v).normalized;
                        Put(output, d, unchecked((ushort)(short)Mathf.RoundToInt(Mathf.Clamp(v.x, -1, 1) * 32767))); Put(output, d + 2, unchecked((ushort)(short)Mathf.RoundToInt(Mathf.Clamp(v.y, -1, 1) * 32767))); Put(output, d + 4, unchecked((ushort)(short)Mathf.RoundToInt(Mathf.Clamp(v.z, -1, 1) * 32767)));
                    }
                Array.Clear(output, dest + weights.Item1, 16); Array.Clear(output, dest + boneIds.Item1, 8);
                var ordered = influences.OrderByDescending(v => v.Value).ThenBy(v => v.Key).ToArray(); int remaining = 65535;
                for (int j = 0; j < ordered.Length; j++)
                {
                    int weight = j == ordered.Length - 1 ? remaining : Math.Min(remaining, (int)Math.Round(ordered[j].Value * 65535.0 / sum));
                    remaining -= weight; Put(output, dest + weights.Item1 + j * 2, (ushort)weight);
                    output[dest + boneIds.Item1 + j] = checked((byte)Array.IndexOf(palette, ordered[j].Key));
                }
            }
            byte[] record = (byte[])raw.Record.Clone(); Put(record, 0x3e, (ushort)((U16(record, 0x3e) & 0xff00) | raw.Stride));
            return new OutputMesh { Record = record, Layout = layout, Vertices = output, Indices = indices.ToArray(), Palette = palette, Bounds = bounds, PreserveSkinning = raw.PreserveSkinning };
        }
        static void PutBounds(byte[] bytes, int offset, Bounds bounds)
        {
            Put(bytes, offset, bounds.center.x); Put(bytes, offset + 4, bounds.center.y); Put(bytes, offset + 8, bounds.center.z); Put(bytes, offset + 12, 1f);
            Put(bytes, offset + 16, bounds.extents.x); Put(bytes, offset + 20, bounds.extents.y); Put(bytes, offset + 24, bounds.extents.z);
        }
    }
}

