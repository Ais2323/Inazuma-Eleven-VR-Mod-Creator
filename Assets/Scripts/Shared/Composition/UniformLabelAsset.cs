using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using G4Runtime;

namespace G4Composition
{
    // Number and shirt-name meshes share the native n*.g4md reference, but can
    // be selected independently and joined back into that single reference.
    internal static class UniformLabelAsset
    {
        static readonly Dictionary<string, string[]> meshNames = new();
        internal static bool IsName(string name) => name.StartsWith("name_", StringComparison.OrdinalIgnoreCase);
        internal static bool HasPart(string mesh, bool name)
        {
            if (string.IsNullOrEmpty(mesh)) return false;
            string path = Path.Combine(DumpDataManager.CommonPath, "chr", mesh);
            if (!File.Exists(path)) return false;
            if (!meshNames.TryGetValue(path, out var names)) meshNames.Add(path, names = UniformFbxWriter.Slots(File.ReadAllBytes(path)));
            return names.Any(n => IsName(n) == name);
        }

        internal static RawHeadAsset Read(string mesh, string texture, G4Skeleton rig)
        {
            string path = Path.Combine(DumpDataManager.CommonPath, "chr", mesh);
            var asset = new RawHeadAsset { RelativePath = mesh + "|" + texture, ModelPath = path, Metadata = File.ReadAllBytes(path),
                Geometry = File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")), Skeleton = rig,
                TexturePath = Path.Combine(DumpDataManager.Dx11Path, "chr", texture) };
            asset.Preview = G4ModelParser.Parse(asset.Metadata, asset.Geometry, path);
            var b = asset.Reader; int table = b.U16(4), count = b.U16(0x20), cursor = table + count * 0x50;
            for (int i = 0; i < b.U8(0x26); i++) { int size = 8 + b.U8(cursor + 1) * 8; asset.Layouts.Add(asset.Metadata.Skip(cursor).Take(size).ToArray()); cursor += size; }
            for (int i = 0; i < count; i++) {
                int at = table + i * 0x50, stride = b.U16(at + 0x3e) & 255; if (stride == 0) stride = 0x44;
                int vertices = b.Int(at + 8), indices = b.Int(at + 12), paletteStart = b.U16(at + 0x3a);
                int paletteCount = (b.U16(at + 0x3c) & 0x100) != 0 ? b.U8(at + 0x3c) : 0;
                asset.Meshes.Add(new RawMesh { Index = i, Record = asset.Metadata.Skip(at).Take(0x50).ToArray(), Stride = stride, VertexCount = vertices,
                    Layout = b.U8(at + 0x42), Material = b.U8(at + 0x43), PreserveSkinning = true,
                    Vertices = asset.Geometry.Skip(b.Int(at)).Take(vertices * stride).ToArray(),
                    Indices = Enumerable.Range(0, indices).Select(j => BitConverter.ToUInt16(asset.Geometry, b.Int(0x5c) + b.Int(at + 4) + j * 2)).ToArray(),
                    Palette = Enumerable.Range(0, paletteCount).Select(j => b.U32(asset.Section(0x74) + b.U16(asset.Section(0x82) + (paletteStart + j) * 2) * 4)).ToArray() });
            }
            asset.TextureContainer = File.ReadAllBytes(asset.TexturePath); asset.Textures.AddRange(RawTexture.Read(asset.TextureContainer, asset.TexturePath)); return asset;
        }

        internal static G4CompositionWriter.Result Combine(RawHeadAsset number, RawHeadAsset name)
        {
            var sources = new Dictionary<string, RawHeadAsset>(); var document = new CompositionDocument();
            void Add(RawHeadAsset source, bool names) {
                if (source == null) return;
                sources[source.RelativePath] = source;
                var slots = UniformFbxWriter.Slots(source.Metadata);
                for (int i = 0; i < slots.Length; i++) if (IsName(slots[i]) == names && source.Meshes[i].Indices.Length > 0)
                    document.Parts.Add(new CompositionPart { Name = slots[i], MeshIndex = i, SourceRelativePath = source.RelativePath,
                        Triangles = Enumerable.Range(0, source.Meshes[i].Indices.Length / 3).ToArray() });
            }
            Add(number, false); Add(name, true);
            if (document.Parts.Count == 0) throw new InvalidDataException("Selected uniform has no number/name geometry.");
            var target = number ?? name;
            var result = new G4CompositionWriter().Build(document, sources, target, CancellationToken.None, preserveNativeStructure: false);
            // Preserve the native G4TX prefix/header conventions used by retail.
            result.Textures = RawTexture.WriteNativeLayout(target.TextureContainer, result.Textures, RawTexture.Read(result.Textures, "Uniform labels"));
            return result;
        }
    }
}
