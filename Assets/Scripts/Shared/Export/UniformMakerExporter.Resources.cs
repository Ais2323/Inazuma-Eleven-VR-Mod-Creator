using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G4Composition;
using G4Runtime;

public static partial class UniformMakerExporter
{
    static string[] BuildResource(string mesh, string texture, UniformPartKind kind, bool split,
        UniformMakerVariant variant, string key, Dictionary<string, byte[]> converted, Dictionary<string, byte[]> pending, UniformCatalog catalog, string ownerName = null, string modelName = null)
    {
        string stem = "um_" + Digest(ResourceIdentity(mesh, texture, kind, split, variant, catalog, ownerName, modelName));
        string modelRelative = "_uniform/" + stem + "/" + (modelName ?? Path.GetFileName(mesh));
        // Keep the target kit's texture owner name for its native portrait lookup;
        // the private folder separates the edited payload from unrelated uniforms.
        string textureRelative = "_uniform/" + stem + "/" + (ownerName ?? stem + ".g4tx");
        string mdOutput = "common/chr/" + modelRelative;
        if (pending.ContainsKey(mdOutput)) return new[] { modelRelative, textureRelative };
        byte[] metadata = File.ReadAllBytes(Path.Combine(DumpDataManager.CommonPath, "chr", mesh));
        byte[] geometry = File.ReadAllBytes(Path.ChangeExtension(Path.Combine(DumpDataManager.CommonPath, "chr", mesh), ".g4mg"));
        if (kind == UniformPartKind.Upper && variant.fbx != null) {
            var rig = NativeBodyShape.Skeleton(catalog.BodyByAttribute(variant.bodyAttribute));
            var imported = UniformFbxWriter.Build(metadata, geometry, variant.fbx, rig); metadata = imported.Metadata; geometry = imported.Geometry;
        }
        byte[] container = File.ReadAllBytes(Path.Combine(DumpDataManager.Dx11Path, "chr", texture));
        var parsed = G4ModelParser.Parse(metadata, geometry, mesh);
        var nativeTextures = RawTexture.Read(container, texture);
        var byHash = nativeTextures.ToDictionary(t => t.Hash);
        byte[] Payload(RawTexture original, UniformPartKind part)
        {
            int channel = G4TextureParser.IsBase(original.Name) ? 0 : Regex.IsMatch(original.Name, @"oc(?:\.\d+|\.a)?$", RegexOptions.IgnoreCase) ? 1 :
                Regex.IsMatch(original.Name, @"msk(?:\.\d+|\.a)?$", RegexOptions.IgnoreCase) ? 2 :
                Regex.IsMatch(original.Name, @"spm?(?:\.\d+|\.a)?$", RegexOptions.IgnoreCase) ? 3 : -1;
            var edit = variant.textures.SingleOrDefault(t => t.part == part && t.channel == channel && t.originalName == original.Name);
            if (edit == null) return original.Dds;
            if (!converted.TryGetValue(TextureIdentity(edit), out var dds)) converted.Add(TextureIdentity(edit), dds = UniformTextureCodec.ToDds(edit));
            var expected = G4TextureParser.ParseDDS(original.Dds, original.Name);
            var actual = G4TextureParser.ParseDDS(dds, edit.fileName);
            if (actual.Width != expected.Width || actual.Height != expected.Height || actual.MipCount != expected.MipCount)
                throw new InvalidDataException("Imported uniform DDS dimensions or mip count differ from the source.");
            return dds;
        }
        var replacements = new Dictionary<uint, byte[]>();
        bool sharedConflict = false;
        foreach (var material in parsed.Materials)
        {
            var part = split && material.Name.EndsWith("_30M", StringComparison.OrdinalIgnoreCase) ? UniformPartKind.Lower : kind;
            foreach (uint hash in material.TextureHashes)
                if (byHash.TryGetValue(hash, out var original))
                {
                    byte[] payload = Payload(original, part);
                    if (replacements.TryGetValue(hash, out var previous) && !previous.SequenceEqual(payload)) sharedConflict = true;
                    else replacements[hash] = payload;
                }
        }
        if (!sharedConflict)
        {
            // Native upper/lower usually already have separate texture entries.
            // Preserve every shader, UV, skin and LOD binding byte-for-byte.
            bool changed = nativeTextures.Any(t => replacements.TryGetValue(t.Hash, out var dds) && !t.Dds.SequenceEqual(dds));
            byte[] nativeContainer = changed ? RawTexture.WriteNativeLayout(container, container, nativeTextures.Select(t => new RawTexture {
                Name = t.Name, Hash = t.Hash, Descriptor = t.Descriptor, Dds = replacements.TryGetValue(t.Hash, out var dds) ? dds : t.Dds }).ToArray()) : container;
            pending.Add(mdOutput, metadata); pending.Add(Path.ChangeExtension(mdOutput, ".g4mg"), geometry);
            pending.Add("dx11/chr/" + textureRelative, nativeContainer);
            return new[] { modelRelative, textureRelative };
        }
        int Section(byte[] data, int field) => BitConverter.ToUInt16(data, 10) * 4 + BitConverter.ToUInt16(data, field) * 4;
        int materialTable = Section(metadata, 0x64), refTable = Section(metadata, 0x68), hashTable = Section(metadata, 0x76);
        int materialCount = BitConverter.ToUInt16(metadata, 0x22), oldHashCount = metadata[0x27];
        var hashes = Enumerable.Range(0, oldHashCount).Select(i => BitConverter.ToUInt32(metadata, hashTable + i * 4)).ToList();
        var outputTextures = new List<RawTexture>(nativeTextures);
        var textureSlots = new Dictionary<string, int>(StringComparer.Ordinal);
        var refs = new List<byte>(); var starts = new int[materialCount];
        for (int material = 0; material < materialCount; material++)
        {
            int record = materialTable + material * 16;
            int first = BitConverter.ToUInt16(metadata, record + 6), count = metadata[record + 12];
            starts[material] = refs.Count / 6;
            var part = split && parsed.Materials[material].Name.EndsWith("_30M", StringComparison.OrdinalIgnoreCase) ? UniformPartKind.Lower : kind;
            for (int i = 0; i < count; i++)
            {
                var reference = metadata.Skip(refTable + (first + i) * 6).Take(6).ToArray();
                if (reference.Length != 6) throw new InvalidDataException("Invalid uniform material reference.");
                int slot = reference[0];
                if (slot < oldHashCount && byHash.TryGetValue(hashes[slot], out var original))
                {
                    string identity = part + "/" + original.Name;
                    if (!textureSlots.TryGetValue(identity, out int outputSlot))
                    {
                        byte[] dds = Payload(original, part);
                        string name = G4TextureParser.IsBase(original.Name) && original.Name == Path.GetFileNameWithoutExtension(texture)
                            ? stem + (part == UniformPartKind.Lower ? "_lower" : "") : stem + "_" + part + "_" + original.Name;
                        outputSlot = hashes.Count;
                        if (outputSlot > 254) throw new InvalidDataException("Uniform texture slot capacity exceeded.");
                        uint hash = G4TextureParser.Crc32(name);
                        if (hashes.Contains(hash)) throw new InvalidDataException("Uniform texture hash collision.");
                        hashes.Add(hash); textureSlots.Add(identity, outputSlot);
                        outputTextures.Add(new RawTexture { Name = name, Hash = hash, Descriptor = (byte[])original.Descriptor.Clone(), Dds = dds });
                    }
                    reference[0] = checked((byte)outputSlot);
                }
                refs.AddRange(reference);
            }
        }
        metadata = G4CompositionWriter.ReplaceNativeSection(metadata, 0x76, hashes.SelectMany(BitConverter.GetBytes).ToArray());
        metadata = G4CompositionWriter.ReplaceNativeSection(metadata, 0x68, refs.ToArray());
        metadata[0x27] = checked((byte)hashes.Count);
        materialTable = Section(metadata, 0x64);
        for (int material = 0; material < materialCount; material++)
            BitConverter.GetBytes(checked((ushort)starts[material])).CopyTo(metadata, materialTable + material * 16 + 6);
        byte[] textures = RawTexture.WriteNativeLayout(container, container, outputTextures);
        var check = G4ModelParser.Parse(metadata, geometry, "Exported uniform");
        var textureCheck = G4TextureParser.Parse(textures, "Exported uniform textures", new List<string>(), baseOnly: false);
        foreach (var material in check.Materials)
            if (G4TextureParser.Choose(material, textureCheck) == null) throw new InvalidDataException("Exported uniform material has no base texture: " + material.Name);
        pending.Add(mdOutput, metadata);
        pending.Add(Path.ChangeExtension(mdOutput, ".g4mg"), geometry);
        pending.Add("dx11/chr/" + textureRelative, textures);
        return new[] { modelRelative, textureRelative };
    }
}
