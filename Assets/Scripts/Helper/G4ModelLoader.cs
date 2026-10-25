using System.Collections.Generic;
using System.IO;
using G4Runtime;
using G4Composition;
using UnityEngine;

public static class G4ModelLoader
{
    public static GameObject CreateModel(
        string modelPath,
        string modelName,
        string textureName = null,
        string texturePath = null,
        bool fallbackToFirstTexture = false,
        bool applyNodeBindPose = false,
        IReadOnlyDictionary<uint, AvatarBoneTransform> boneTransforms = null,
        string skeletonModelHint = null,
        bool loadTextures = true,
        G4Skeleton skeletonOverride = null,
        bool headLocalSkinning = true)
    {
        // ??頝臬?
        var modelRoot = Path.Combine(DumpDataManager.CommonPath, modelPath);
        var textureRoot = Path.Combine(DumpDataManager.Dx11Path, texturePath ?? modelPath);

        // ??g4md?4mg瑼?雿蔭
        var meta = File.ReadAllBytes(Path.Combine(modelRoot, $"{modelName}.g4md"));
        var geometry = File.ReadAllBytes(Path.Combine(modelRoot, $"{modelName}.g4mg"));

        // 閫??璅∪?
        var model = G4ModelParser.Parse(meta, geometry, modelRoot);
        G4Skeleton skeleton = skeletonOverride ?? (applyNodeBindPose ? LoadEditSkeleton(modelRoot, skeletonModelHint ?? modelName) : null);
        Matrix4x4[] deformedBind = skeleton == null ? null : BuildDeformedBind(skeleton, boneTransforms);
        var warnings = new List<string>();
        if (textureName == null) textureName = modelName;
        var textures = loadTextures ? G4TextureParser.Parse(
            File.ReadAllBytes(Path.Combine(textureRoot, $"{textureName}.g4tx")),
            textureRoot,
            warnings,
            default,
            false) : new List<G4TextureParser.TextureData>();

        var modelObject = new GameObject(modelName);
        var resources = modelObject.AddComponent<RuntimeHeadResources>();
        if (!applyNodeBindPose && modelPath.Replace((char)92, '/').StartsWith("chr/_face/", System.StringComparison.OrdinalIgnoreCase))
            modelObject.AddComponent<HeadAttachmentAnchor>().SourceModelPath = Path.Combine(modelRoot, modelName + ".g4md");
        var shader = Resources.Load<Shader>("G4HeadPreview");
        var materials = new Material[model.Materials.Count];
        Vector3[] faceDeformed = null;
        int[] faceTriangles = null;

        for (int i = 0; i < model.Materials.Count; i++)
        {
            var materialData = model.Materials[i];
            var material = resources.Own(new Material(shader));
            var textureData = G4TextureParser.Choose(materialData, textures);
            if (textureData == null && fallbackToFirstTexture)
                textureData = textures.Find(value => G4TextureParser.IsBase(value.Name));
            if (textureData != null) material.mainTexture = CreateTexture(resources, textureData, false);
            var ocData = G4TextureParser.ChooseSuffix(materialData, textures, "oc");
            if (ocData != null) material.SetTexture("_OcTex", CreateTexture(resources, ocData, true));
            var spmData = G4TextureParser.ChooseSuffix(materialData, textures, "spm");
            if (spmData != null) material.SetTexture("_SpmTex", CreateTexture(resources, spmData, true));
            var mskData = G4TextureParser.ChooseSuffix(materialData, textures, "msk");
            if (mskData != null)
            {
                material.SetTexture("_MskTex", CreateTexture(resources, mskData, true));
                material.SetFloat("_HasMsk", 1f);
            }
            materials[i] = material;
        }

        foreach (var part in model.Parts)
        {
            var mesh = resources.Own(new Mesh());
            mesh.name = part.Name;
            mesh.vertices = part.Positions;
            mesh.uv = part.UV;
            mesh.colors32 = part.Colors;
            mesh.triangles = part.Indices;
            if (part.Normals != null) mesh.normals = part.Normals;
            else mesh.RecalculateNormals();
            mesh.RecalculateBounds();

            if (skeleton != null && part.BonePalette != null)
                ApplyDirectDeformation(mesh, part, skeleton, deformedBind, headLocalSkinning);

            if (part.Name.StartsWith("base_", System.StringComparison.OrdinalIgnoreCase))
            {
                faceDeformed = mesh.vertices;
                faceTriangles = part.Indices;
            }
            else if (part.Name.StartsWith("parts_", System.StringComparison.OrdinalIgnoreCase) &&
                faceDeformed != null && faceTriangles != null)
            {
                ProjectFacialPartsToFace(mesh, faceDeformed, faceTriangles);
            }

            if (skeleton != null && part.BonePalette == null && skeleton.ByHash.TryGetValue(part.NodeHash, out int bone))
            {
                Matrix4x4 reflection = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
                Matrix4x4 bind = reflection * skeleton.Bind[bone] * reflection;
                Matrix4x4 changedNodeBind = reflection * deformedBind[bone] * reflection;
                Vector3[] positions = mesh.vertices;
                Vector3[] normals = mesh.normals;
                for (int vertex = 0; vertex < positions.Length; vertex++)
                {
                    Vector3 basePosition = bind.MultiplyPoint3x4(positions[vertex]);
                    Vector3 baseNormal = normals != null && vertex < normals.Length ? bind.inverse.transpose.MultiplyVector(normals[vertex]).normalized : Vector3.zero;
                    if (part.BonePalette == null)
                    {
                        // Some facial features, including the nose, are rigid
                        // mesh parts assigned directly to a skeleton node and
                        // therefore have no per-vertex bone palette.
                        positions[vertex] = changedNodeBind.MultiplyPoint3x4(positions[vertex]);
                        if (normals != null && vertex < normals.Length)
                            normals[vertex] = changedNodeBind.inverse.transpose.MultiplyVector(normals[vertex]).normalized;
                    }
                }
                mesh.vertices = positions;
                if (normals != null) mesh.normals = normals;
                mesh.RecalculateBounds();
            }

            var partObject = new GameObject(part.Name, typeof(MeshFilter), typeof(MeshRenderer));
            partObject.transform.SetParent(modelObject.transform, false);
            partObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            partObject.GetComponent<MeshRenderer>().sharedMaterial = materials[part.Material];
            if (skeleton != null && modelName.StartsWith("hair", System.StringComparison.OrdinalIgnoreCase))
                partObject.AddComponent<AvatarMeshSkin>().Capture(part, skeleton);
        }

        return modelObject;
    }

    static void ApplyDirectDeformation(
        Mesh mesh,
        G4ModelParser.Part part,
        G4Skeleton skeleton,
        Matrix4x4[] deformedBind, bool headLocalSkinning)
    {
        Matrix4x4 reflection = Matrix4x4.Scale(new Vector3(1f, 1f, -1f));
        Matrix4x4 bindShape = Matrix4x4.identity;
        // Shared edit-face vertices are stored relative to c_head_1_0, while
        // G4SK bind matrices are in the full character space. Convert into
        // that space before skinning and back into mesh-local space after it.
        uint headHash = G4TextureParser.Crc32("c_head_1_0");
        if (headLocalSkinning && skeleton.ByHash.TryGetValue(headHash, out int headBone))
            bindShape = reflection * skeleton.Bind[headBone] * reflection;
        Matrix4x4 inverseBindShape = bindShape.inverse;
        bool rigidEar = part.BonePalette.Length == 1 &&
            part.Name.StartsWith("ear", System.StringComparison.OrdinalIgnoreCase);
        var skin = new Matrix4x4[part.BonePalette.Length];
        for (int i = 0; i < skin.Length; i++)
        {
            if (!skeleton.ByHash.TryGetValue(part.BonePalette[i], out int bone))
                throw new InvalidDataException(part.Name + ": palette bone 0x" + part.BonePalette[i].ToString("X8") + " is absent from the edit skeleton.");
            Matrix4x4 changed = reflection * deformedBind[bone] * reflection;
            Matrix4x4 inverseBind = reflection * skeleton.InverseBind[bone] * reflection;
            // earL/earR vertices are authored in their individual bone-local
            // space. Their bind transform is placement, not something to
            // cancel as with the face's model-space skinning.
            skin[i] = rigidEar
                ? inverseBindShape * changed
                : inverseBindShape * changed * inverseBind * bindShape;
        }
        Vector3[] positions = mesh.vertices;
        Vector3[] normals = mesh.normals;
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            float total = 0f;
            Vector3 position = Vector3.zero;
            Vector3 normal = Vector3.zero;
            for (int influence = 0; influence < 8; influence++)
                total += part.BoneWeights[vertex, influence];
            if (total <= 0f) continue;
            for (int influence = 0; influence < 8; influence++)
            {
                ushort raw = part.BoneWeights[vertex, influence];
                if (raw == 0) continue;
                int palette = part.BoneIndices[vertex, influence];
                float weight = raw / total;
                position += skin[palette].MultiplyPoint3x4(positions[vertex]) * weight;
                if (normals != null && vertex < normals.Length)
                    normal += skin[palette].inverse.transpose.MultiplyVector(normals[vertex]) * weight;
            }
            positions[vertex] = position;
            if (normals != null && vertex < normals.Length) normals[vertex] = normal.normalized;
        }
        mesh.vertices = positions;
        if (normals != null) mesh.normals = normals;
        mesh.RecalculateBounds();
    }

    static void ProjectFacialPartsToFace(Mesh mesh, Vector3[] face, int[] triangles)
    {
        Vector3[] positions = mesh.vertices;
        for (int vertex = 0; vertex < positions.Length; vertex++)
        {
            float best = float.PositiveInfinity;
            Vector3 bestPoint = positions[vertex], bestNormal = Vector3.zero;
            for (int index = 0; index < triangles.Length; index += 3)
            {
                Vector3 a = face[triangles[index]], b = face[triangles[index + 1]], c = face[triangles[index + 2]];
                Vector3 point = ClosestPointOnTriangle(positions[vertex], a, b, c, out _);
                float distance = (positions[vertex] - point).sqrMagnitude;
                if (distance >= best) continue;
                best = distance;
                bestPoint = point;
                bestNormal = Vector3.Cross(b - a, c - a).normalized;
            }
            // SP-node sliders are already applied. Conform the resulting part
            // to the actual deformed head once, keeping a small depth offset.
            positions[vertex] = bestPoint + bestNormal * .0005f;
        }
        mesh.vertices = positions;
        mesh.RecalculateBounds();
    }
    internal static Vector3 ClosestPointOnTriangle(Vector3 p, Vector3 a, Vector3 b, Vector3 c, out Vector3 barycentric)
    {
        Vector3 ab = b - a, ac = c - a, ap = p - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0f && d2 <= 0f) { barycentric = new Vector3(1f, 0f, 0f); return a; }
        Vector3 bp = p - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0f && d4 <= d3) { barycentric = new Vector3(0f, 1f, 0f); return b; }
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0f && d1 >= 0f && d3 <= 0f)
        {
            float v = d1 / (d1 - d3); barycentric = new Vector3(1f - v, v, 0f); return a + ab * v;
        }
        Vector3 cp = p - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0f && d5 <= d6) { barycentric = new Vector3(0f, 0f, 1f); return c; }
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0f && d2 >= 0f && d6 <= 0f)
        {
            float w = d2 / (d2 - d6); barycentric = new Vector3(1f - w, 0f, w); return a + ac * w;
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0f && d4 - d3 >= 0f && d5 - d6 >= 0f)
        {
            float w = (d4 - d3) / ((d4 - d3) + (d5 - d6));
            barycentric = new Vector3(0f, 1f - w, w); return b + (c - b) * w;
        }
        float denominator = 1f / (va + vb + vc);
        float insideV = vb * denominator, insideW = vc * denominator;
        barycentric = new Vector3(1f - insideV - insideW, insideV, insideW);
        return a + ab * insideV + ac * insideW;
    }

    static Matrix4x4[] BuildDeformedBind(G4Skeleton skeleton, IReadOnlyDictionary<uint, AvatarBoneTransform> transforms)
    {
        var result = new Matrix4x4[skeleton.Bind.Length];
        for (int i = 0; i < result.Length; i++)
        {
            int parent = skeleton.Parents[i];
            bool root = parent < 0 || parent >= result.Length || parent == i;
            Matrix4x4 local = root ? skeleton.Bind[i] : skeleton.Bind[parent].inverse * skeleton.Bind[i];
            if (transforms != null && transforms.TryGetValue(skeleton.Hashes[i], out AvatarBoneTransform value))
            {
                // Character-edit position values are offsets of the node's local
                // translation in parent space. Post-multiplying a translation
                // would rotate it by the bone's own axes (the nose then moves
                // toward the chin instead of upward).
                local.m03 += value.Position.x;
                local.m13 += value.Position.y;
                local.m23 += value.Position.z;
                local *= Matrix4x4.Rotate(Quaternion.Euler(value.Rotation * Mathf.Rad2Deg));
                local *= Matrix4x4.Scale(value.Scale);
            }
            result[i] = root ? local : result[parent] * local;
        }
        return result;
    }
    static G4Skeleton LoadEditSkeleton(string modelRoot, string modelName)
    {
        string folder = new DirectoryInfo(modelRoot).Name;
        // Ear meshes are weighted to l_ear1_1_0/r_ear1_1_0 in the matching
        // body edit skeleton. _earSK contains a different ear module hierarchy
        // and does not contain the palette bones referenced by ear00x.g4md.
        string skeletonFolder = folder.StartsWith("_hair", System.StringComparison.OrdinalIgnoreCase) ? "_hairSK" : "_bodySK";
        string skeletonName = skeletonFolder;
        if (skeletonFolder == "_bodySK")
        {
            if (modelName.StartsWith("face53", System.StringComparison.OrdinalIgnoreCase)) skeletonName = "c000201_edit";
            else if (modelName.StartsWith("face55", System.StringComparison.OrdinalIgnoreCase) || modelName.StartsWith("face65", System.StringComparison.OrdinalIgnoreCase)) skeletonName = "c000301_edit";
            else if (modelName.StartsWith("face56", System.StringComparison.OrdinalIgnoreCase) || modelName.StartsWith("face66", System.StringComparison.OrdinalIgnoreCase)) skeletonName = "c000401_edit";
            else skeletonName = "c000101_edit";
        }
        string path = skeletonFolder == "_bodySK"
            ? Path.Combine(Directory.GetParent(modelRoot).FullName, skeletonFolder, skeletonName, skeletonName + ".g4sk")
            : Path.Combine(Directory.GetParent(modelRoot).FullName, skeletonFolder, skeletonFolder + ".g4sk");
        if (!File.Exists(path)) return null;
        try { return G4Skeleton.Read(File.ReadAllBytes(path), path, "20_EDIT preview skeleton"); }
        catch (System.Exception exception) { Debug.LogWarning("Cannot load preview skeleton " + path + ": " + exception.Message); return null; }
    }

    static Texture2D CreateTexture(RuntimeHeadResources resources, G4TextureParser.TextureData data, bool linear)
    {
        var texture = resources.Own(new Texture2D(data.Width, data.Height, data.Format, data.MipCount, linear)
        {
            name = data.Name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        });
        texture.LoadRawTextureData(data.Pixels);
        texture.Apply(false, true);
        return texture;
    }
}


