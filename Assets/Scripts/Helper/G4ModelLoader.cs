using System.Collections.Generic;
using System.IO;
using G4Runtime;
using UnityEngine;

public static class G4ModelLoader
{
    public static GameObject CreateModel(
        string modelPath,
        string modelName,
        string textureName = null,
        string texturePath = null)
    {
        // 取得路徑
        var modelRoot = Path.Combine(DumpDataManager.CommonPath, modelPath);
        var textureRoot = Path.Combine(DumpDataManager.Dx11Path, texturePath ?? modelPath);

        // 取得g4md與g4mg檔案位置
        var meta = File.ReadAllBytes(Path.Combine(modelRoot, $"{modelName}.g4md"));
        var geometry = File.ReadAllBytes(Path.Combine(modelRoot, $"{modelName}.g4mg"));

        // 解析模型
        var model = G4ModelParser.Parse(meta, geometry, modelRoot);
        var warnings = new List<string>();
        if (textureName == null) textureName = modelName;
        var textures = G4TextureParser.Parse(
            File.ReadAllBytes(Path.Combine(textureRoot, $"{textureName}.g4tx")),
            textureRoot,
            warnings,
            default,
            false);

        var modelObject = new GameObject(modelName);
        var resources = modelObject.AddComponent<RuntimeHeadResources>();
        var shader = Resources.Load<Shader>("G4HeadPreview");
        var materials = new Material[model.Materials.Count];

        for (int i = 0; i < model.Materials.Count; i++)
        {
            var materialData = model.Materials[i];
            var material = resources.Own(new Material(shader));
            var textureData = G4TextureParser.Choose(materialData, textures);
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

            var partObject = new GameObject(part.Name, typeof(MeshFilter), typeof(MeshRenderer));
            partObject.transform.SetParent(modelObject.transform, false);
            partObject.GetComponent<MeshFilter>().sharedMesh = mesh;
            partObject.GetComponent<MeshRenderer>().sharedMaterial = materials[part.Material];
        }

        return modelObject;
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
