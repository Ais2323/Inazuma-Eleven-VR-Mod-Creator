using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using G4Composition;
using G4Runtime;
using SFB;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    public Button fbxExportButton;

    sealed class ExportMeshSource
    {
        internal string Path;
        internal UniformPartKind Kind;
        internal int Slot;
    }
    readonly Dictionary<MeshFilter, ExportMeshSource> exportMeshSources = new();

    public void OnClickExportFbx() => ExportFbx(null);

    internal void ExportPartFbx(UniformPartKind kind) => ExportFbx(kind);

    void ExportFbx(UniformPartKind? kind)
    {
        if (model == null || CurrentBody == null) return;
        string name = isNewUniform && uniformNameInput != null && !string.IsNullOrWhiteSpace(uniformNameInput.text)
            ? uniformNameInput.text : catalog.Entries[selected].Key;
        name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        if (kind.HasValue) name += "_" + kind.Value;
        name += "_" + side + "_" + role + "_" + CurrentBody.Int(4);
        string path = StandaloneFileBrowser.SaveFilePanel(LocalizationManager.GetText(kind.HasValue ? "UniformFbxExportPart" : "UniformFbxExport"),
            SettingManager.GetExportDirectory("uniformFbxExportDirectory"), name, "fbx");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase)) path += ".fbx";
        try {
            WriteSelectedFbx(path, kind);
            SettingManager.RememberExportDirectory("uniformFbxExportDirectory", Path.GetDirectoryName(Path.GetFullPath(path)));
            Debug.Log("[UniformMaker] Exported FBX: " + path, this);
        }
        catch (Exception error) { Debug.LogError("[UniformMaker FBX] " + error.Message, this); }
    }

    internal void WriteOutfitFbx(string path) => WriteSelectedFbx(path, null);

    internal void WriteSelectedFbx(string path, UniformPartKind? kind)
    {
        if (model == null || CurrentBody == null) throw new InvalidOperationException("Select a uniform first.");
        UpdateNameColor();
        path = Path.GetFullPath(path);
        string textureFolder = Path.GetFileNameWithoutExtension(path) + "_Textures";
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        var scene = CaptureSelectedFbx(textureFolder, files, kind);
        if (scene.Meshes.Count == 0) throw new InvalidOperationException("No visible model in the selected part.");
        byte[] fbx = BinaryFbxExporter.Encode(scene);
        // Validate the emitted binary through the same importer used by players.
        var check = UniformFbxReader.Read(fbx);
        if (check.Parts.Length != scene.Meshes.Count || check.Parts.Sum(p => p.Positions.Length) != scene.Meshes.Sum(m => m.Triangles.Length))
            throw new InvalidDataException("FBX geometry validation failed.");
        string directory = Path.GetDirectoryName(path);
        Directory.CreateDirectory(directory);
        foreach (var file in files) {
            string target = Path.Combine(directory, file.Key);
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            // Content-addressed textures allow the FBX to be committed last.
            if (!File.Exists(target) || !File.ReadAllBytes(target).SequenceEqual(file.Value)) File.WriteAllBytes(target, file.Value);
        }
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllBytes(temporary, fbx);
            if (File.Exists(path)) File.Replace(temporary, path, null);
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    BinaryFbxExporter.Scene CaptureOutfitFbx(string textureFolder, Dictionary<string, byte[]> files)
        => CaptureSelectedFbx(textureFolder, files, null);

    BinaryFbxExporter.Scene CaptureSelectedFbx(string textureFolder, Dictionary<string, byte[]> files, UniformPartKind? selectedKind)
    {
        var rig = NativeBodyShape.Skeleton(CurrentBody);
        var mirror = Matrix4x4.Scale(new Vector3(1, 1, -1));
        var scene = new BinaryFbxExporter.Scene {
            BoneNames = rig.Names, Parents = rig.Parents,
            Armature = BinaryFbxExporter.ArmatureName + "_B" + CurrentBody.Int(4) + "_C" + chestSize + "_H" + hem,
            Bind = G4ModelLoader.BuildDeformedBind(rig, NativeBodyShape.Outfit(CurrentBody, chestSize, hem)).Select(m => mirror * m * mirror).ToArray()
        };
        var parsed = new Dictionary<string, G4ModelParser.Model>();
        var materials = new Dictionary<Material, int>();
        var texturePaths = new Dictionary<Texture, string>();
        var textureContents = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in exportMeshSources) {
            var filter = pair.Key;
            var renderer = filter != null ? filter.GetComponent<Renderer>() : null;
            if (renderer == null || !renderer.enabled || !filter.gameObject.activeInHierarchy || filter.sharedMesh == null || filter.sharedMesh.vertexCount == 0) continue;
            var source = pair.Value;
            string sourceKey = source.Kind + "/" + source.Path;
            if (!parsed.TryGetValue(sourceKey, out var native)) {
                string modelPath = Path.Combine(DumpDataManager.CommonPath, "chr", source.Path);
                var imported = BuildPartFbx(source.Path, source.Kind);
                native = G4ModelParser.Parse(imported?.Metadata ?? File.ReadAllBytes(modelPath),
                    imported?.Geometry ?? File.ReadAllBytes(Path.ChangeExtension(modelPath, ".g4mg")), modelPath);
                parsed.Add(sourceKey, native);
            }
            var part = native.Parts.Single(p => p.SourceMeshIndex == source.Slot);
            UniformPartKind kind = source.Kind;
            if (kind == UniformPartKind.Upper && UniformFbxWriter.MatchesPart(native, source.Slot, UniformPartKind.Lower)) kind = UniformPartKind.Lower;
            if (selectedKind.HasValue ? kind != selectedKind.Value : kind != UniformPartKind.Upper && kind != UniformPartKind.Lower) continue;
            var mesh = filter.sharedMesh;
            var material = renderer.sharedMaterial;
            if (!materials.TryGetValue(material, out int materialIndex)) {
                materialIndex = scene.Materials.Count;
                materials.Add(material, materialIndex);
                scene.Materials.Add(new BinaryFbxExporter.Surface {
                    Name = kind + "_" + material.name,
                    UvScale = material.mainTextureScale, UvOffset = material.mainTextureOffset,
                    Color = material.HasProperty("_WholeSkin") && material.GetFloat("_WholeSkin") > .5f ? SelectedSkinColor : Color.white,
                    Diffuse = TexturePath(material, "_MainTex"), Oc = TexturePath(material, "_OcTex"),
                    Mask = TexturePath(material, "_MskTex"), Spm = TexturePath(material, "_SpmTex")
                });
            }
            Matrix4x4 transform = model.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            var geometry = new BinaryFbxExporter.Geometry {
                Name = kind + "__" + Path.GetFileNameWithoutExtension(source.Path) + "_" + source.Slot,
                Positions = mesh.vertices.Select(transform.MultiplyPoint3x4).ToArray(),
                Normals = mesh.normals.Select(transform.inverse.transpose.MultiplyVector).Select(n => n.normalized).ToArray(),
                Uv = mesh.uv,
                Colors = mesh.colors32, Triangles = mesh.triangles, Material = materialIndex,
                Bones = new int[mesh.vertexCount, 8], Weights = new float[mesh.vertexCount, 8]
            };
            if (part.Positions.Length != mesh.vertexCount) throw new InvalidDataException("Preview/source vertex count differs: " + filter.name);
            for (int v = 0; v < mesh.vertexCount; v++) {
                float total = 0;
                if (part.BonePalette != null)
                    for (int w = 0; w < 8; w++) total += part.BoneWeights[v, w];
                if (total <= 0) {
                    geometry.Bones[v, 0] = rig.ByHash.TryGetValue(part.NodeHash, out int rigid) ? rigid : 0;
                    geometry.Weights[v, 0] = 1;
                }
                else for (int w = 0; w < 8; w++) {
                    if (part.BoneWeights[v, w] == 0) continue;
                    geometry.Bones[v, w] = rig.ByHash[part.BonePalette[part.BoneIndices[v, w]]];
                    geometry.Weights[v, w] = part.BoneWeights[v, w] / total;
                }
            }
            scene.Meshes.Add(geometry);
        }
        return scene;

        string TexturePath(Material material, string property)
        {
            Texture texture = material.HasProperty(property) ? material.GetTexture(property) : null;
            if (texture == null) return null;
            if (texturePaths.TryGetValue(texture, out string existing)) return existing;
            byte[] png = UniformTextureCodec.ToPng(texture, property != "_MainTex");
            string digest;
            using (var hash = SHA256.Create()) digest = BitConverter.ToString(hash.ComputeHash(png)).Replace("-", "").ToLowerInvariant();
            if (textureContents.TryGetValue(digest, out existing)) { texturePaths.Add(texture, existing); return existing; }
            string name = Path.GetFileNameWithoutExtension(texture.name);
            name = string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
            if (string.IsNullOrEmpty(name)) name = "Texture";
            string relative = textureFolder + "/" + name + "_" + digest.Substring(0, 20) + ".png";
            textureContents.Add(digest, relative);
            files[relative] = png; texturePaths.Add(texture, relative);
            return relative;
        }
    }
}
