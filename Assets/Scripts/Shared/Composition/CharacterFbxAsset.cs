using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using G4Runtime;
using G4Composition;
using UnityEngine;

// FBX replaces geometry in native head slots. Material/animation slots and
// texture bindings stay native, so eyes and mouth retain their game contract.
internal static class CharacterFbxAsset
{
    internal static RawHeadAsset Read(CharacterMakerPart part, bool applyImport = true)
    {
        if (part == null || string.IsNullOrEmpty(part.sourceId)) throw new InvalidDataException("Select a source head model first.");
        string relative = CharacterMakerBatchExporter.FindFaceModel(string.IsNullOrEmpty(part.avatarCode) ? part.sourceId : "c01000010");
        RawHeadAsset asset;
        if (string.IsNullOrEmpty(part.avatarCode)) asset = RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path, relative, CancellationToken.None);
        else {
            var avatar = AvatarCompositionAsset.Build(part.avatarCode);
            var target = RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path, relative, CancellationToken.None);
            var document = new CompositionDocument();
            Vector3 anchor = HeadAttachmentAnchor.Get(target);
            foreach (var p in avatar.Preview.Parts) document.Parts.Add(new CompositionPart {
                SourceRelativePath = avatar.RelativePath, MeshIndex = p.SourceMeshIndex, Name = p.Name,
                Position = anchor, Scale = Vector3.one, Triangles = Enumerable.Range(0, p.Indices.Length / 3).ToArray()
            });
            var generated = new G4CompositionWriter().Build(document, new Dictionary<string, RawHeadAsset> { [avatar.RelativePath] = avatar }, target, CancellationToken.None);
            asset = RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path, relative, CancellationToken.None, generated.Metadata, generated.Geometry);
            asset.LegacyHairAtlasBounds=avatar.LegacyHairAtlasBounds;
            asset.TextureContainer = generated.Textures; asset.Textures.Clear(); asset.Textures.AddRange(RawTexture.Read(generated.Textures, "Avatar FBX textures"));
        }
        var rig = Rig(asset);
        if (applyImport && part.fbx != null && !part.fbx.IsEmpty) {
            var slots = new HashSet<int>(part.fbx.meshSlots);
            var generated = UniformFbxWriter.Build(asset.Metadata, asset.Geometry, part.fbx, rig, slots.Contains);
            var replacement = RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path, relative, CancellationToken.None, generated.Metadata, generated.Geometry);
            replacement.LegacyHairAtlasBounds=asset.LegacyHairAtlasBounds;
            replacement.TextureContainer = asset.TextureContainer; replacement.Textures.Clear(); replacement.Textures.AddRange(asset.Textures);
            asset = replacement;
        }
        asset.Skeleton = rig;
        asset.RelativePath = "Fbx/" + part.sourceId + "/" + RawHeadAsset.Digest(asset.Metadata.Concat(asset.Geometry).ToArray()) + ".g4md";
        foreach (var mesh in asset.Meshes) mesh.PreserveSkinning = true;
        return asset;
    }

    internal static List<string> NativeNames(CharacterMakerPart part,RawHeadAsset asset,int option) {
        var selected=new HashSet<string>(part.meshNames,StringComparer.OrdinalIgnoreCase);
        if(asset.Preview.Parts.Any(p=>selected.Contains(p.Name)))return part.meshNames;
        if(!string.IsNullOrEmpty(part.avatarCode))
            return asset.Preview.Parts.Where(p=>option==1?p.Name.StartsWith("eye_",StringComparison.OrdinalIgnoreCase)||p.Name.StartsWith("mouth_",StringComparison.OrdinalIgnoreCase):!p.Name.StartsWith("eye_",StringComparison.OrdinalIgnoreCase)&&!p.Name.StartsWith("mouth_",StringComparison.OrdinalIgnoreCase)).Take(option==1?2:1).Select(p=>p.Name).ToList();
        return part.meshNames;
    }

    internal static G4Skeleton Rig(RawHeadAsset asset) => asset.Skeleton ??= new SkeletonResolver(DumpDataManager.CommonPath).Resolve(asset, CancellationToken.None);

    internal static GameObject Preview(RawHeadAsset asset, string sourceId)
    {
        string folder = Path.GetDirectoryName(asset.ModelPath).Substring(DumpDataManager.CommonPath.TrimEnd('\\','/').Length + 1);
        return G4ModelLoader.CreateModel(folder, Path.GetFileNameWithoutExtension(asset.ModelPath), metadataOverride: asset.Metadata,
            geometryOverride: asset.Geometry, textureContainerOverride: asset.TextureContainer);
    }

    internal static BinaryFbxExporter.Scene Capture(RawHeadAsset asset, HashSet<string> names, Func<int, Material> materialFor,
        Func<Material, string, string> texturePath)
    {
        var rig = Rig(asset); var mirror = Matrix4x4.Scale(new Vector3(1,1,-1));
        var scene = new BinaryFbxExporter.Scene { BoneNames = rig.Names, Parents = rig.Parents,
            Bind = rig.Bind.Select(m => mirror * m * mirror).ToArray(), Armature = "CharMakerHeadExport" };
        var materials = new Dictionary<int,int>();
        foreach (var part in asset.Preview.Parts.Where(p => names.Contains(p.Name))) {
            if (!materials.TryGetValue(part.Material, out int materialIndex)) {
                materialIndex = scene.Materials.Count; materials.Add(part.Material,materialIndex);
                Material material = materialFor(part.SourceMeshIndex);
                scene.Materials.Add(new BinaryFbxExporter.Surface { Name = asset.Preview.Materials[part.Material].Name,
                    Diffuse = texturePath(material,"_MainTex"), Oc = texturePath(material,"_OcTex"), Spm = texturePath(material,"_SpmTex"),
                    Mask = texturePath(material,"_MskTex"), UvScale = material != null ? material.mainTextureScale : Vector2.one,
                    UvOffset = material != null ? material.mainTextureOffset : Vector2.zero });
            }
            var mesh = new BinaryFbxExporter.Geometry { Name = part.Name, Positions = part.Positions, Normals = part.Normals,
                Uv = part.UV, Colors = part.Colors, Triangles = part.Indices, Material = materialIndex,
                Bones = new int[part.Positions.Length,8], Weights = new float[part.Positions.Length,8] };
            for (int v=0;v<part.Positions.Length;v++) {
                float sum = 0;
                if(part.BonePalette != null) for(int w=0;w<8;w++) sum += part.BoneWeights[v,w];
                if(sum<=0) { mesh.Bones[v,0] = rig.ByHash.TryGetValue(part.NodeHash,out int node) ? node : rig.ByHash[G4TextureParser.Crc32("c_head_1_0")]; mesh.Weights[v,0]=1; }
                else for(int w=0;w<8;w++) if(part.BoneWeights[v,w]>0) {
                    mesh.Bones[v,w]=rig.ByHash[part.BonePalette[part.BoneIndices[v,w]]]; mesh.Weights[v,w]=part.BoneWeights[v,w]/sum;
                }
            }
            scene.Meshes.Add(mesh);
        }
        if(scene.Meshes.Count==0)throw new InvalidDataException("No selected head meshes to export.");
        return scene;
    }
}
