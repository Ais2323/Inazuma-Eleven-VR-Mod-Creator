using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace G4Runtime
{
    public sealed class HeadEntry
    {
        public string RelativePath { get; }
        public string DisplayName => Path.ChangeExtension(RelativePath, null).Replace('\\', '/');
        internal HeadEntry(string path) { RelativePath = path; }
    }
    public sealed class HeadLoadResult
    {
        public GameObject Root { get; }
        public IReadOnlyList<string> Warnings { get; }
        internal HeadLoadResult(GameObject root, List<string> warnings) { Root = root; Warnings = warnings.AsReadOnly(); }
    }
    /// <summary>Call from Unity's main thread; the service captures the current validated dump paths.</summary>
    public sealed class HeadModelLoader
    {
        readonly string faceRoot, textureRoot;
        public HeadModelLoader()
        {
            if (DumpDataManager.CommonPath == null || DumpDataManager.Dx11Path == null) throw new InvalidOperationException("Validate DumpDataManager before creating a loader.");
            faceRoot = Path.Combine(DumpDataManager.CommonPath, "chr", "_face");
            textureRoot = Path.Combine(DumpDataManager.Dx11Path, "chr", "_face");
        }
        public Task<IReadOnlyList<HeadEntry>> GetHeadsAsync(CancellationToken token = default) => Task.Run<IReadOnlyList<HeadEntry>>(() =>
        {
            var entries = new List<HeadEntry>();
            foreach (string path in Directory.EnumerateFiles(faceRoot, "*.g4md", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                if (File.Exists(Path.ChangeExtension(path, ".g4mg"))) entries.Add(new HeadEntry(path.Substring(faceRoot.Length + 1)));
            }
            return entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToArray();
        }, token);
        sealed class Payload
        {
            public G4ModelParser.Model Model;
            public List<G4TextureParser.TextureData> Textures;
        }
        public async Task<HeadLoadResult> LoadHeadAsync(HeadEntry entry, CancellationToken token = default)
        {
            if (SynchronizationContext.Current == null) throw new InvalidOperationException("LoadHeadAsync must be called on Unity's main thread.");
            if (entry == null) throw new ArgumentNullException(nameof(entry));
            string path = Path.GetFullPath(Path.Combine(faceRoot, entry.RelativePath));
            if (!path.StartsWith(faceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Head path outside dump.");
            var payload = await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                var model = G4ModelParser.Parse(File.ReadAllBytes(path), File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg")), path, token);
                var textures = new List<G4TextureParser.TextureData>();
                string texturePath = FindTexture(entry.RelativePath, model.Warnings, token);
                if (texturePath != null)
                {
                    try { textures = G4TextureParser.Parse(File.ReadAllBytes(texturePath), texturePath, model.Warnings, token, false); }
                    catch (Exception e) when (e is InvalidDataException || e is IOException || e is UnauthorizedAccessException || e is OverflowException) { model.Warnings.Add(e.Message); }
                }
                else model.Warnings.Add("No G4TX found for " + entry.DisplayName);
                return new Payload { Model = model, Textures = textures };
            }, token);
            token.ThrowIfCancellationRequested();
            var root = new GameObject(Path.GetFileNameWithoutExtension(entry.RelativePath));
            root.SetActive(false);
            var owner = root.AddComponent<RuntimeHeadResources>();
            try
            {
                Shader shader = Resources.Load<Shader>("G4HeadPreview");
                if (shader == null) throw new InvalidOperationException("Missing Resources/G4HeadPreview.shader.");
                var materials = new Material[payload.Model.Materials.Count];
                var usedMaterials = new HashSet<int>(payload.Model.Parts.Select(p => p.Material));
                var textures = new Dictionary<G4TextureParser.TextureData, Texture2D>();
                for (int i = 0; i < materials.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    if (!usedMaterials.Contains(i)) continue;
                    var surface = payload.Model.Materials[i];
                    var material = owner.Own(new Material(shader) { name = surface.Name });
                    materials[i] = material;
                    var data = G4TextureParser.Choose(surface, payload.Textures);
                    if (data != null)
                    {
                        if (!textures.TryGetValue(data, out var texture))
                        {
                            if (!SystemInfo.SupportsTextureFormat(data.Format))
                            {
                                payload.Model.Warnings.Add(data.Name + ": GPU does not support " + data.Format);
                                continue;
                            }
                            texture = owner.Own(new Texture2D(data.Width, data.Height, data.Format, data.MipCount, false) { name = data.Name, wrapMode = TextureWrapMode.Repeat, filterMode = FilterMode.Bilinear });
                            texture.LoadRawTextureData(data.Pixels);
                            texture.Apply(false, true);
                            textures.Add(data, texture);
                        }
                        material.mainTexture = texture;
                    }
                    else
                    {
                        material.color = new Color(0.75f, 0.75f, 0.78f);
                        payload.Model.Warnings.Add(surface.Name + ": base texture not found.");
                    }
                    var ocData = G4TextureParser.ChooseSuffix(surface, payload.Textures, "oc");
                    if (ocData != null)
                    {
                        var ocTexture = CreateTexture(ocData, true, textures, owner, payload.Model.Warnings);
                        if (ocTexture != null) material.SetTexture("_OcTex", ocTexture);
                    }
                    var spmData = G4TextureParser.ChooseSuffix(surface, payload.Textures, "spm");
                    if (spmData != null)
                    {
                        var spmTexture = CreateTexture(spmData, true, textures, owner, payload.Model.Warnings);
                        if (spmTexture != null) material.SetTexture("_SpmTex", spmTexture);
                    }
                    await Task.Yield();
                }
                foreach (var part in payload.Model.Parts)
                {
                    token.ThrowIfCancellationRequested();
                    var mesh = owner.Own(new Mesh { name = part.Name, indexFormat = part.Positions.Length > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16 });
                    mesh.vertices = part.Positions; mesh.uv = part.UV; mesh.colors32 = part.Colors; mesh.triangles = part.Indices;
                    if (part.Normals != null) mesh.normals = part.Normals; else mesh.RecalculateNormals();
                    mesh.RecalculateBounds();
                    var child = new GameObject(part.Name, typeof(MeshFilter), typeof(MeshRenderer));
                    child.transform.SetParent(root.transform, false);
                    child.GetComponent<MeshFilter>().sharedMesh = mesh;
                    child.GetComponent<MeshRenderer>().sharedMaterial = materials[part.Material];
                    await Task.Yield();
                }
                token.ThrowIfCancellationRequested();
                root.SetActive(true);
                return new HeadLoadResult(root, payload.Model.Warnings);
            }
            catch
            {
                owner.Release();
                if (Application.isPlaying) UnityEngine.Object.Destroy(root); else UnityEngine.Object.DestroyImmediate(root);
                throw;
            }
        }
        static Texture2D CreateTexture(G4TextureParser.TextureData data, bool linear, Dictionary<G4TextureParser.TextureData, Texture2D> textures, RuntimeHeadResources owner, List<string> warnings)
        {
            if (textures.TryGetValue(data, out var cached)) return cached;
            if (!SystemInfo.SupportsTextureFormat(data.Format))
            {
                warnings.Add(data.Name + ": GPU does not support " + data.Format);
                return null;
            }
            var texture = owner.Own(new Texture2D(data.Width, data.Height, data.Format, data.MipCount, linear)
            {
                name = data.Name,
                wrapMode = TextureWrapMode.Repeat,
                filterMode = FilterMode.Bilinear
            });
            texture.LoadRawTextureData(data.Pixels);
            texture.Apply(false, true);
            textures.Add(data, texture);
            return texture;
        }
        string FindTexture(string relative, List<string> warnings, CancellationToken token)
        {
            string direct = Path.ChangeExtension(Path.Combine(textureRoot, relative), ".g4tx");
            if (File.Exists(direct)) return direct;
            string parent = Path.ChangeExtension(Path.GetDirectoryName(direct), ".g4tx");
            if (File.Exists(parent)) return parent;
            if (!Directory.Exists(textureRoot)) return null;
            string match = null;
            foreach (string file in Directory.EnumerateFiles(textureRoot, Path.GetFileName(direct), SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested();
                if (match != null) { warnings.Add("Ambiguous G4TX name: " + Path.GetFileName(direct)); return null; }
                match = file;
            }
            return match;
        }
    }
}
