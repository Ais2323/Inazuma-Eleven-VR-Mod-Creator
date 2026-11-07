using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;

public static partial class UniformMakerExporter
{
    static string[] BuildLabels(string originalMesh, string originalTexture, UniformMakerVariant variant, CfgTable.Row body,
        string key, Dictionary<string, byte[]> converted, Dictionary<string, byte[]> pending)
    {
        var rig = NativeBodyShape.Skeleton(body);
        RawHeadAsset Source(UniformPartKind kind, uint id) {
            if (variant.IsHidden(kind)) return null;
            var selection = id == 0 ? null : NativeUniformFashion.Resolve(NativeUniformFashion.ModelName(id), body);
            string mesh = id == 0 ? originalMesh : selection?.Number, texture = id == 0 ? originalTexture : selection?.NumberTexture;
            if (string.IsNullOrEmpty(mesh) || !UniformLabelAsset.HasPart(mesh, kind == UniformPartKind.Name)) return null;
            var asset = UniformLabelAsset.Read(mesh, texture, rig); asset.RelativePath += "|" + kind;
            for (int i = 0; i < asset.Textures.Count; i++) {
                var original = asset.Textures[i];
                var edit = variant.textures.SingleOrDefault(t => t.part == kind && t.originalName == original.Name);
                if (edit == null) continue;
                if (!converted.TryGetValue(TextureIdentity(edit), out var dds)) converted.Add(TextureIdentity(edit), dds = UniformTextureCodec.ToDds(edit));
                var expected = G4Runtime.G4TextureParser.ParseDDS(original.Dds, original.Name); var actual = G4Runtime.G4TextureParser.ParseDDS(dds, edit.fileName);
                if (actual.Width != expected.Width || actual.Height != expected.Height || actual.MipCount != expected.MipCount)
                    throw new InvalidDataException("Imported label DDS dimensions or mip count differ from the source.");
                asset.Textures[i] = new RawTexture { Name = original.Name, Hash = original.Hash, Descriptor = original.Descriptor, Dds = dds };
            }
            return asset;
        }
        var number = Source(UniformPartKind.Number, variant.numberModelId); var name = Source(UniformPartKind.Name, variant.NameModelId);
        if (number == null && name == null) return null;
        var result = UniformLabelAsset.Combine(number, name);
        string folder = "_uniform/um_" + Digest(key + "|labels"), stem = Path.GetFileName(originalMesh ?? (number ?? name).ModelPath);
        string model = folder + "/" + stem, texturePath = folder + "/labels.g4tx";
        pending.Add("common/chr/" + model, result.Metadata); pending.Add("common/chr/" + Path.ChangeExtension(model, ".g4mg"), result.Geometry);
        pending.Add("dx11/chr/" + texturePath, result.Textures); return new[] { model, texturePath };
    }
}
