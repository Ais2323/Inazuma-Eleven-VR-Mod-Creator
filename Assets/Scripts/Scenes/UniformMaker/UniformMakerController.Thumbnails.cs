using System;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;
using UnityEngine;

public sealed partial class UniformMakerController
{
    GameObject CreateModelSample(UniformPartKind kind, UniformCatalog.ModelOption option)
    {
        var body = CurrentBody;
        var rig = NativeBodyShape.Skeleton(body);
        var root = new GameObject("Uniform model thumbnail");
        try
        {
            GameObject Load(string mesh, string texture)
            {
                var part = G4ModelLoader.CreateModel(Path.Combine("chr", Path.GetDirectoryName(mesh)),
                    Path.GetFileNameWithoutExtension(mesh), Path.GetFileNameWithoutExtension(texture),
                    Path.Combine("chr", Path.GetDirectoryName(texture)), skeletonOverride: rig, headLocalSkinning: false);
                part.transform.SetParent(root.transform, false);
                foreach (var renderer in part.GetComponentsInChildren<Renderer>())
                    renderer.sharedMaterial.SetColor("_SkinColor", SelectedSkinColor);
                return part;
            }
            if (kind == UniformPartKind.Gloves)
            {
                var gloves = catalog.ResolveGloves(option.Id, body);
                if (gloves == null) { ReleaseSample(root); return null; }
                Load(gloves.Model, gloves.Texture);
            }
            else
            {
                var clothes = kind == UniformPartKind.Upper ? NativeUniformFashion.Resolve(option.Id, body, 0, 0) : NativeUniformFashion.Resolve(NativeUniformFashion.ModelName(option.Id), body);
                if (clothes == null) { ReleaseSample(root); return null; }
                if (kind == UniformPartKind.Skin)
                {
                    if (string.IsNullOrWhiteSpace(clothes.Skin) || string.IsNullOrWhiteSpace(clothes.SkinTexture)) { ReleaseSample(root); return null; }
                    var skin = Load(clothes.Skin, clothes.SkinTexture);
                    foreach (var renderer in skin.GetComponentsInChildren<Renderer>()) renderer.sharedMaterial.SetFloat("_WholeSkin", 1);
                }
                else if (kind == UniformPartKind.Number || kind == UniformPartKind.Name)
                {
                    if (string.IsNullOrWhiteSpace(clothes.Number) || string.IsNullOrWhiteSpace(clothes.NumberTexture)) { ReleaseSample(root); return null; }
                    var number = Load(clothes.Number, clothes.NumberTexture);
                    FilterLabels(number, kind == UniformPartKind.Name);
                    foreach (var renderer in number.GetComponentsInChildren<Renderer>()) if (kind == UniformPartKind.Number) renderer.sharedMaterial.mainTextureOffset = new Vector2(uniformNumber % 10 * .1f, uniformNumber / 10 * .1f);
                }
                else if (kind == UniformPartKind.Armband)
                {
                    if (string.IsNullOrWhiteSpace(clothes.Armband) || string.IsNullOrWhiteSpace(clothes.ArmbandTexture)) { ReleaseSample(root); return null; }
                    Load(clothes.Armband, clothes.ArmbandTexture);
                }
                else
                {
                    Load(clothes.Uniform, clothes.UniformTexture);
                    if (!string.IsNullOrWhiteSpace(clothes.Skin))
                    {
                        var skin = Load(clothes.Skin, clothes.SkinTexture);
                        foreach (var renderer in skin.GetComponentsInChildren<Renderer>()) renderer.sharedMaterial.SetFloat("_WholeSkin", 1);
                    }
                    if (!string.IsNullOrWhiteSpace(clothes.Number))
                    {
                        var number = Load(clothes.Number, clothes.NumberTexture);
                        foreach (var renderer in number.GetComponentsInChildren<Renderer>())
                            renderer.sharedMaterial.mainTextureOffset = new Vector2(uniformNumber % 10 * .1f, uniformNumber / 10 * .1f);
                    }
                    foreach (var filter in root.GetComponentsInChildren<MeshFilter>())
                        if (filter.name.Equals("collar_11", StringComparison.OrdinalIgnoreCase)) filter.gameObject.SetActive(false);
                }
            }
            return root;
        }
        catch { ReleaseSample(root); throw; }
    }

    static Texture2D RenderModelThumbnail(GameObject root)
    {
        var renderers = root.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) throw new InvalidDataException("No model geometry.");
        Bounds bounds = renderers[0].bounds;
        foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
        var layers = root.GetComponentsInChildren<Transform>(true).ToDictionary(t => t, t => t.gameObject.layer);
        foreach (var child in layers.Keys) child.gameObject.layer = 31;
        var cameraObject = new GameObject("Uniform thumbnail camera");
        var camera = cameraObject.AddComponent<Camera>();
        var previous = RenderTexture.active;
        var render = RenderTexture.GetTemporary(192, 192, 24, RenderTextureFormat.ARGB32);
        Texture2D image = null;
        try
        {
            camera.enabled = false;
            camera.clearFlags = CameraClearFlags.SolidColor;
            camera.backgroundColor = Color.clear;
            camera.cullingMask = 1 << 31;
            camera.orthographic = true;
            camera.orthographicSize = Mathf.Max(.02f, Mathf.Max(bounds.extents.x, bounds.extents.y) * 1.12f);
            camera.nearClipPlane = .01f; camera.farClipPlane = 100;
            camera.transform.SetPositionAndRotation(bounds.center + Vector3.back * (bounds.size.z + 2), Quaternion.identity);
            camera.targetTexture = render;
            camera.Render();
            RenderTexture.active = render;
            image = new Texture2D(192, 192, TextureFormat.RGBA32, false);
            image.ReadPixels(new Rect(0, 0, 192, 192), 0, 0); image.Apply(false, true);
            return image;
        }
        catch { DestroyAsset(image); throw; }
        finally
        {
            RenderTexture.active = previous;
            camera.targetTexture = null;
            RenderTexture.ReleaseTemporary(render);
            DestroyAsset(cameraObject);
            foreach (var pair in layers) if (pair.Key != null) pair.Key.gameObject.layer = pair.Value;
        }
    }

    static void ReleaseSample(GameObject sample)
    {
        if (sample == null) return;
        sample.SetActive(false);
        foreach (var resources in sample.GetComponentsInChildren<RuntimeHeadResources>(true)) resources.Release();
        DestroyAsset(sample);
    }
}
