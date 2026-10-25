using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using UnityEngine.Rendering;
using G4Runtime;
using UnityEngine;
using static UnityEngine.Object;

namespace G4Composition
{
    public static class AvatarHeadBuilder
    {
        /// <summary>Create a head; the caller manages replacement of the displayed model.</summary>
        public static GameObject Build(AvatarRecipeData data, Transform parent = null, int previewExpression = 0, int previewMouthExpression = 4)
        {
            if (data == null) throw new ArgumentNullException(nameof(data));
            // Resolve the selected resources before allocating the new head.
            AvatarHeadParts parts = AvatarRecipeParser.ResolveHeadParts(data);
            var faceBones = AvatarRecipeParser.ResolveFaceBoneTransforms(data);
            var earBones = AvatarRecipeParser.ResolveEarBoneTransforms(data, parts.Ear);
            foreach (var bone in faceBones) if (!earBones.ContainsKey(bone.Key)) earBones.Add(bone.Key, bone.Value);
            var attachment = AvatarRecipeParser.ResolveHeadAttachment(data);
            var rootObject = new GameObject("AvatarRecipeHead");
            rootObject.AddComponent<RuntimeHeadResources>();
            rootObject.AddComponent<HeadAttachmentAnchor>().HasFixedPosition = true;
            var root = rootObject.transform;
            root.SetParent(parent, false);
            try
            {
                // Build the selected face, facial textures and attachments.
                GameObject baseHead = Add(root, "_facebase", parts.HeadModel, parts.FaceTexture, "_facetex/00_face", true, faceBones);
                if (baseHead == null) throw new InvalidDataException("Cannot load the avatar face model: " + parts.HeadModel);
                ApplyHeadAttachment(baseHead, attachment);
                Vector3 headOrigin = BoundsCenter(baseHead);
                ApplyColor(baseHead, RecipeColor(data, 0), true);

                GameObject hairBack = Add(root, "_hairB", parts.HairBack, parts.HairBack + "M", "_hairB", loadTextures: false);
                GameObject hairFront = Add(root, "_hairF", parts.HairFront, parts.HairFront + "M", "_hairF", loadTextures: false);
                GameObject hairUpper = Add(root, "_hairU", parts.HairUpper, parts.HairUpper + "M", "_hairU", loadTextures: false);
                ApplyHeadAttachment(hairBack, attachment);
                ApplyHeadAttachment(hairFront, attachment);
                ApplyHeadAttachment(hairUpper, attachment);
                GameObject ear = Add(root, "_ear", parts.Ear, "_facebase", "_facebase", true, earBones, parts.HeadModel);
                GameObject accessory = Add(root, "_accessory", parts.Accessory, "accessory_10", "_accessorytex", true);

                Texture2D facialParts = ComposeFacialParts(baseHead, data, parts, previewExpression, previewMouthExpression);
                ApplyFacialPartsTexture(baseHead, facialParts);

                // Face and hair vertices share head-local coordinates. Center the assembled
                // object once; translating hair by the face bounds would raise the hairline.
                root.localPosition = -headOrigin;

                Color skin = RecipeColor(data, 0);
                ApplyMaskedSkinColor(ear, skin);
                Color hairColor = RecipeColor(data, 1, category: 2);
                var hairDetails = LoadHairDetailMaps();
                ApplyHairBaseColor(hairBack, hairColor, hairDetails);
                ApplyHairBaseColor(hairFront, hairColor, hairDetails);
                ApplyHairBaseColor(hairUpper, hairColor, hairDetails);
                ApplyColor(accessory, hairColor, false);
                CombineHeadMeshes(root, baseHead, ear, hairBack, hairFront, hairUpper, accessory);
                return rootObject;
            }
            catch
            {
                rootObject.SetActive(false);
                if (Application.isPlaying) UnityEngine.Object.Destroy(rootObject);
                else UnityEngine.Object.DestroyImmediate(rootObject);
                throw;
            }
        }
        // Bake source transforms into head-local vertices, then atlas the material tiles.
        private static void CombineHeadMeshes(Transform root, GameObject face, GameObject ear, params GameObject[] hair)
        {
            var eyes = new List<MeshFilter>();
            var mouths = new List<MeshFilter>();
            var hairs = new List<MeshFilter>();
            foreach (var filter in face.GetComponentsInChildren<MeshFilter>())
            {
                if (filter.name.StartsWith("parts_mouth_", StringComparison.OrdinalIgnoreCase)) mouths.Add(filter);
                else eyes.Add(filter);
            }
            if (ear != null) eyes.AddRange(ear.GetComponentsInChildren<MeshFilter>());
            foreach (var part in hair)
                if (part != null) hairs.AddRange(part.GetComponentsInChildren<MeshFilter>());

            var owner = root.GetComponent<RuntimeHeadResources>();
            Material faceMaterial = CreateSharedFaceMaterial(face, owner);
            CombineFaceMesh(root, "eye_10", eyes, faceMaterial, owner);
            CombineFaceMesh(root, "mouth_10", mouths, faceMaterial, owner);
            CombineMeshGroup(root, "hair_10", hairs, owner);

            // Final meshes and baked atlases are independent of the source resources.
            // Discard the intermediate RGBA sheets as well as the source geometry.
            var sources = new List<GameObject> { face, ear };
            sources.AddRange(hair);
            foreach (var source in sources)
            {
                if (source == null) continue;
                var sourceOwner = source.GetComponent<RuntimeHeadResources>();
                foreach (var renderer in source.GetComponentsInChildren<MeshRenderer>())
                    foreach (var material in renderer.sharedMaterials) sourceOwner.Own(material);
                source.SetActive(false);
                source.transform.SetParent(null, false);
                if (Application.isPlaying) Destroy(source); else DestroyImmediate(source);
            }
        }

        // Native 01_IE1 faces use one 2048x1024, 4x2 expression sheet for both
        // eye_10 and mouth_10. The head shell and ears sample its plain skin edge.
        private static Material CreateSharedFaceMaterial(GameObject face, RuntimeHeadResources owner)
        {
            Texture foundation = face.transform.Find("base_eye_10").GetComponent<Renderer>().sharedMaterial.mainTexture;
            Texture expressions = face.transform.Find("parts_eye_10").GetComponent<Renderer>().sharedMaterial.mainTexture;
            var shader = Resources.Load<Shader>("AvatarNativeFaceAtlas");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Missing native face atlas shader.");
            var bake = new Material(shader);
            RenderTexture projectedFeature = ProjectFaceFeature(face, foundation);
            bake.SetTexture("_FeatureTex", projectedFeature);
            // The first corner of 00_face is the unpainted skin color.
            var feature = foundation as Texture2D;
            Color skin = feature.GetPixel(0, 0);
            bake.SetColor("_SkinColor", QualitySettings.activeColorSpace == ColorSpace.Linear ? skin.linear : skin);
            const int width = 2048, height = 1024, mipCount = 9;
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            RenderTexture previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            Texture2D pixels = null;
            try
            {
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(expressions, target, bake);
                RenderTexture.active = target;
                pixels = new Texture2D(width, height, TextureFormat.RGBA32, mipCount, false);
                pixels.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                pixels.Apply(true, false);
                Texture2D atlas;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.CompressTexture(pixels, TextureFormat.BC7, UnityEditor.TextureCompressionQuality.Best);
                atlas = owner.Own(pixels);
                pixels = null;
#else
                using (var blocks = new MemoryStream())
                {
                    for (int mip = 0; mip < mipCount; mip++)
                    {
                        byte[] dds = Bc7PortraitEncoder.Encode(pixels.GetPixels32(mip), width >> mip, height >> mip, default);
                        blocks.Write(dds, 148, dds.Length - 148);
                    }
                    atlas = owner.Own(new Texture2D(width, height, TextureFormat.BC7, mipCount, false));
                    atlas.LoadRawTextureData(blocks.ToArray());
                    atlas.Apply(false, false);
                }
#endif
                atlas.name = "AvatarFace_10";
                atlas.wrapMode = TextureWrapMode.Clamp;
                atlas.filterMode = FilterMode.Bilinear;
                var material = owner.Own(new Material(Resources.Load<Shader>("G4HeadPreview")) { name = "AvatarFace_10" });
                material.mainTexture = atlas;
                material.SetFloat("_OcDarkness", 1f);
                material.SetFloat("_SpmStrength", 0f);
                return material;
            }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSrgb;
                RenderTexture.ReleaseTemporary(target);
                DestroyImmediate(bake);
                RenderTexture.ReleaseTemporary(projectedFeature);
                if (pixels != null) DestroyImmediate(pixels);
            }
        }

        private static RenderTexture ProjectFaceFeature(GameObject face, Texture feature)
        {
            var skinMesh = face.transform.Find("base_eye_10").GetComponent<MeshFilter>();
            Vector3[] skinVertices = skinMesh.sharedMesh.vertices;
            Vector2[] skinUv = skinMesh.sharedMesh.uv;
            int[] skinTriangles = skinMesh.sharedMesh.triangles;
            var shader = Resources.Load<Shader>("AvatarFaceFeatureProjection");
            if (shader == null || !shader.isSupported) throw new InvalidOperationException("Missing face feature projection shader.");
            var material = new Material(shader);
            material.mainTexture = feature;
            var target = RenderTexture.GetTemporary(2048, 1024, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            var temporary = new List<Mesh>();
            bool success = false;
            var commands = new CommandBuffer { name = "Avatar feature UV projection" };
            try
            {
                commands.SetRenderTarget(target);
                commands.SetViewport(new Rect(0, 0, target.width, target.height));
                GL.sRGBWrite = QualitySettings.activeColorSpace == ColorSpace.Linear;
                Color skin = ((Texture2D)feature).GetPixel(0, 0);
                commands.ClearRenderTarget(false, true, QualitySettings.activeColorSpace == ColorSpace.Linear ? skin.linear : skin);
                foreach (string name in new[] { "parts_eye_10", "parts_mouth_10" })
                {
                    var filter = face.transform.Find(name).GetComponent<MeshFilter>();
                    Mesh source = filter.sharedMesh;
                    Vector3[] vertices = source.vertices;
                    Vector2[] uv = source.uv;
                    var featureUv = new Vector2[vertices.Length];
                    Matrix4x4 toSkin = skinMesh.transform.worldToLocalMatrix * filter.transform.localToWorldMatrix;
                    for (int v = 0; v < vertices.Length; v++)
                    {
                        Vector3 point = toSkin.MultiplyPoint3x4(vertices[v]);
                        float best = float.PositiveInfinity;
                        for (int t = 0; t < skinTriangles.Length; t += 3)
                        {
                            int a = skinTriangles[t], b = skinTriangles[t + 1], c = skinTriangles[t + 2];
                            Vector3 closest = G4ModelLoader.ClosestPointOnTriangle(point, skinVertices[a], skinVertices[b], skinVertices[c], out Vector3 bary);
                            float distance = (closest - point).sqrMagnitude;
                            if (distance >= best) continue;
                            best = distance;
                            featureUv[v] = skinUv[a] * bary.x + skinUv[b] * bary.y + skinUv[c] * bary.z;
                        }
                    }
                    var mesh = new Mesh();
                    temporary.Add(mesh);
                    var positions = new Vector3[uv.Length];
                    for (int v = 0; v < uv.Length; v++) positions[v] = new Vector3(uv[v].x, uv[v].y, 0f);
                    mesh.vertices = positions;
                    mesh.uv = featureUv;
                    mesh.triangles = source.triangles;
                    var properties = new MaterialPropertyBlock();
                    for (int cell = 0; cell < 8; cell++)
                    {
                        properties.SetVector("_CellOffset", new Vector4(cell % 4 * .25f, cell / 4 * .5f, 0f, 0f));
                        commands.DrawMesh(mesh, Matrix4x4.identity, material, 0, 0, properties);
                    }
                }
                Graphics.ExecuteCommandBuffer(commands);
                success = true;
                return target;
            }
            finally
            {
                commands.Release();
                GL.sRGBWrite = previousSrgb;
                RenderTexture.active = previous;
                foreach (var mesh in temporary) DestroyImmediate(mesh);
                DestroyImmediate(material);
                if (!success) RenderTexture.ReleaseTemporary(target);
            }
        }
        private static void CombineFaceMesh(Transform root, string name, List<MeshFilter> filters, Material material, RuntimeHeadResources owner)
        {
            var instances = new List<CombineInstance>();
            var copies = new List<Mesh>();
            try
            {
                foreach (var filter in filters)
                {
                    Mesh mesh = filter.sharedMesh;
                    if (mesh == null) continue;
                    bool expression = filter.name.StartsWith("parts_eye_", StringComparison.OrdinalIgnoreCase) ||
                        filter.name.StartsWith("parts_mouth_", StringComparison.OrdinalIgnoreCase);
                    if (!expression)
                    {
                        mesh = Instantiate(mesh);
                        copies.Add(mesh);
                        var uv = new Vector2[mesh.vertexCount];
                        for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(1.5f / 2048f, 1.5f / 1024f);
                        mesh.uv = uv;
                    }
                    for (int submesh = 0; submesh < mesh.subMeshCount; submesh++)
                        instances.Add(new CombineInstance { mesh = mesh, subMeshIndex = submesh,
                            transform = root.worldToLocalMatrix * filter.transform.localToWorldMatrix });
                }
                if (instances.Count == 0) return;
                var combined = owner.Own(new Mesh { name = name, indexFormat = IndexFormat.UInt32 });
                combined.CombineMeshes(instances.ToArray(), true, true, false);
                combined.RecalculateBounds();
                var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
                go.layer = root.gameObject.layer;
                go.transform.SetParent(root, false);
                go.GetComponent<MeshFilter>().sharedMesh = combined;
                go.GetComponent<MeshRenderer>().sharedMaterial = material;
            }
            finally { foreach (var copy in copies) DestroyImmediate(copy); }
        }
        private static void CombineMeshGroup(Transform root, string name, List<MeshFilter> filters, RuntimeHeadResources owner)
        {
            var instances = new List<CombineInstance>();
            var skins = new List<AvatarMeshSkin>();
            var materials = new List<Material>();
            foreach (var filter in filters)
            {
                Mesh source = filter.sharedMesh;
                if (source == null) continue;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (renderer == null) throw new InvalidOperationException("Missing mesh renderer: " + filter.name);
                Material[] sourceMaterials = renderer.sharedMaterials;
                if (sourceMaterials.Length != source.subMeshCount)
                    throw new InvalidOperationException("Material/submesh count mismatch: " + filter.name);
                for (int submesh = 0; submesh < source.subMeshCount; submesh++)
                {
                    instances.Add(new CombineInstance { mesh = source, subMeshIndex = submesh,
                        transform = root.worldToLocalMatrix * filter.transform.localToWorldMatrix });
                    materials.Add(sourceMaterials[submesh]);
                    skins.Add(filter.GetComponent<AvatarMeshSkin>());
                }
            }
            if (instances.Count == 0) return;

            // Native hair keeps the 128x128 base separate from 512x512 OC/SPM.
            // All three maps use the same tile layout and remapped UVs.
            var unique = new List<Material>();
            foreach (var material in materials) if (!unique.Contains(material)) unique.Add(material);
            // Some authored UV islands extend past 0..1. Include that sampling domain
            // in the baked tile so clamping/repeating stays inside its own atlas island.
            var domains = new List<Rect>();
            foreach (var material in unique)
            {
                Vector2 min = Vector2.zero, max = Vector2.one;
                for (int i = 0; i < instances.Count; i++)
                {
                    if (materials[i] != material) continue;
                    Vector2[] uv = instances[i].mesh.uv;
                    foreach (int vertex in instances[i].mesh.GetIndices(instances[i].subMeshIndex))
                    { min = Vector2.Min(min, uv[vertex]); max = Vector2.Max(max, uv[vertex]); }
                }
                domains.Add(Rect.MinMaxRect(min.x, min.y, max.x, max.y));
            }
            var tiles = new List<Texture2D>();
            var remapped = new List<Mesh>();
            Texture2D atlas = null;
            Rect legacyBounds = new Rect(0, 0, 1, 1);
            Vector2 inset = new Vector2(.5f / 128f, .5f / 128f);
            try
            {
                for (int i = 0; i < unique.Count; i++) tiles.Add(BakeMaterialTile(unique[i], domains[i], 1, 128));
                var packed = new Texture2D(2, 2, TextureFormat.RGBA32, false, false);
                Rect[] rects;
                try
                {
                    rects = packed.PackTextures(tiles.ToArray(), 2, 128, false);
                    legacyBounds = Rect.MinMaxRect(rects.Min(r => r.xMin), rects.Min(r => r.yMin), rects.Max(r => r.xMax), rects.Max(r => r.yMax));
                    inset = new Vector2(.5f / 128f / legacyBounds.width, .5f / 128f / legacyBounds.height);
                    for (int i = 0; i < rects.Length; i++)
                        rects[i] = new Rect((rects[i].x - legacyBounds.x) / legacyBounds.width,
                            (rects[i].y - legacyBounds.y) / legacyBounds.height,
                            rects[i].width / legacyBounds.width, rects[i].height / legacyBounds.height);
                }
                finally { DestroyImmediate(packed); }
                // Re-bake from source maps at the final size; enlarging packed pixels
                // would retain the resolution loss from the old compact layout.
                atlas = CreateHairAtlas(unique, domains, rects, 1, inset, owner);
                Texture2D ocAtlas = CreateHairAtlas(unique, domains, rects, 2, inset, owner);
                Texture2D spmAtlas = CreateHairAtlas(unique, domains, rects, 3, inset, owner);
                atlas.name = name;
                for (int i = 0; i < instances.Count; i++)
                {
                    var instance = instances[i];
                    // Copy per material/submesh: shared vertices may need different UV islands.
                    Mesh copy = Instantiate(instance.mesh);
                    remapped.Add(copy);
                    int materialIndex = unique.IndexOf(materials[i]);
                    Rect tile = rects[materialIndex];
                    Rect domain = domains[materialIndex];
                    // Keep boundary UVs on texel centers, away from atlas padding.
                    // Ears and solid-color parts can use a constant UV at exactly 0/1.
                    float insetX = inset.x, insetY = inset.y;
                    tile = Rect.MinMaxRect(tile.xMin + insetX, tile.yMin + insetY,
                        tile.xMax - insetX, tile.yMax - insetY);
                    Vector2[] uv = copy.uv;
                    for (int v = 0; v < uv.Length; v++)
                        uv[v] = new Vector2(tile.x + (uv[v].x - domain.x) / domain.width * tile.width, tile.y + (uv[v].y - domain.y) / domain.height * tile.height);
                    copy.uv = uv;
                    instance.mesh = copy;
                    instances[i] = instance;
                }
                var mesh = owner.Own(new Mesh { name = name, indexFormat = IndexFormat.UInt32 });
                mesh.CombineMeshes(instances.ToArray(), true, true, false);
                mesh.RecalculateBounds();
                var atlasMaterial = owner.Own(new Material(Resources.Load<Shader>("G4HeadPreview")) { name = name + "_Material" });
                atlasMaterial.mainTexture = atlas;
                atlasMaterial.SetTexture("_OcTex", ocAtlas);
                atlasMaterial.SetTexture("_SpmTex", spmAtlas);
                atlasMaterial.SetFloat("_OcDarkness", unique[0].GetFloat("_OcDarkness"));
                atlasMaterial.SetFloat("_SpmStrength", unique[0].GetFloat("_SpmStrength"));
                var combined = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer));
                combined.transform.SetParent(root, false);
                combined.layer = root.gameObject.layer;
                combined.GetComponent<MeshFilter>().sharedMesh = mesh;
                var combinedRenderer = combined.GetComponent<MeshRenderer>();
                combinedRenderer.sharedMaterial = atlasMaterial;
                var combinedSkin = combined.AddComponent<AvatarMeshSkin>();
                combinedSkin.Combine(skins, instances, mesh.vertexCount);
                combinedSkin.LegacyAtlasBounds = legacyBounds;

            }
            finally
            {
                foreach (var tile in tiles) DestroyImmediate(tile);
                foreach (var copy in remapped) DestroyImmediate(copy);
            }
        }

        private static Texture2D CreateHairAtlas(List<Material> materials, List<Rect> domains, Rect[] rects, int channel, Vector2 inset, RuntimeHeadResources owner)
        {
            int size = channel == 1 ? 128 : 512;
            var pixels = new Color32[size * size];
            Color32 empty = channel == 1 ? (Color32)Color.clear : channel == 2 ? (Color32)Color.white : (Color32)Color.black;
            for (int i = 0; i < pixels.Length; i++) pixels[i] = empty;
            for (int i = 0; i < materials.Count; i++)
            {
                Rect rect = rects[i];
                int x = Mathf.RoundToInt(rect.xMin * size), y = Mathf.RoundToInt(rect.yMin * size);
                int width = Mathf.RoundToInt(rect.xMax * size) - x, height = Mathf.RoundToInt(rect.yMax * size) - y;
                if (width <= 0 || height <= 0) continue;
                // Match the base UVs' half-texel inset at its lower resolution.
                float baseWidth = rect.width * 128f, baseHeight = rect.height * 128f;
                Rect domain = domains[i];
                float scaleX = baseWidth / Mathf.Max(1f, baseWidth - inset.x * 256f), scaleY = baseHeight / Mathf.Max(1f, baseHeight - inset.y * 256f);
                Rect sampling = new Rect(domain.x - domain.width * (scaleX - 1f) * .5f,
                    domain.y - domain.height * (scaleY - 1f) * .5f, domain.width * scaleX, domain.height * scaleY);
                Texture2D tile = BakeMaterialTile(materials[i], sampling, channel, outputWidth: width, outputHeight: height);
                int padding = channel == 1 ? 1 : 4;
                try
                {
                    Color32[] colors = tile.GetPixels32();
                    // Extrude tile edges into padding for filtering.
                    for (int py = Mathf.Max(0, y - padding); py < Mathf.Min(size, y + height + padding); py++)
                    for (int px = Mathf.Max(0, x - padding); px < Mathf.Min(size, x + width + padding); px++)
                        pixels[py * size + px] = colors[Mathf.Clamp(py - y, 0, height - 1) * width + Mathf.Clamp(px - x, 0, width - 1)];
                }
                finally { DestroyImmediate(tile); }
            }
            var texture = owner.Own(new Texture2D(size, size, TextureFormat.RGBA32, channel == 1 ? 6 : 8, channel != 1));
            texture.SetPixels32(pixels);
            texture.Apply(true, false);
            texture = CompressNativeAtlas(texture, channel != 1, owner);
            texture.name = channel == 1 ? "hair_10" : channel == 2 ? "hair_10oc" : "hair_10spm";
            return texture;
        }

        private static Texture2D CompressNativeAtlas(Texture2D pixels, bool linear, RuntimeHeadResources owner)
        {
#if UNITY_EDITOR
            UnityEditor.EditorUtility.CompressTexture(pixels, TextureFormat.BC7, UnityEditor.TextureCompressionQuality.Best);
            Texture2D result = pixels;
#else
            Texture2D result;
            using (var blocks = new MemoryStream())
            {
                for (int mip = 0; mip < pixels.mipmapCount; mip++)
                {
                    byte[] dds = Bc7PortraitEncoder.Encode(pixels.GetPixels32(mip), pixels.width >> mip, pixels.height >> mip, default);
                    blocks.Write(dds, 148, dds.Length - 148);
                }
                result = new Texture2D(pixels.width, pixels.height, TextureFormat.BC7, pixels.mipmapCount, linear);
                result.LoadRawTextureData(blocks.ToArray());
                result.Apply(false, false);
            }
            DestroyImmediate(pixels);
#endif
            result.wrapMode = TextureWrapMode.Clamp;
            result.filterMode = FilterMode.Bilinear;
            return owner.Own(result);
        }
        private static Texture2D BakeMaterialTile(Material source, Rect domain, int channel, int maxSize = 0, int outputWidth = 0, int outputHeight = 0)
        {
            int width = 1, height = 1;
            foreach (string property in new[] { "_MainTex", "_OcTex", "_SpmTex", "_MskTex" })
            {
                Texture texture = source.GetTexture(property);
                if (texture == null) continue;
                width = Mathf.Max(width, texture.width);
                height = Mathf.Max(height, texture.height);
            }
            width = Mathf.CeilToInt(width * domain.width);
            height = Mathf.CeilToInt(height * domain.height);
            if (maxSize > 0 && Mathf.Max(width, height) > maxSize)
            {
                float ratio = maxSize / (float)Mathf.Max(width, height);
                width = Mathf.Max(1, Mathf.RoundToInt(width * ratio));
                height = Mathf.Max(1, Mathf.RoundToInt(height * ratio));
            }
            if (outputWidth > 0) { width = outputWidth; height = outputHeight; }
            if (width > SystemInfo.maxTextureSize || height > SystemInfo.maxTextureSize)
                throw new InvalidOperationException("Avatar texture sampling domain exceeds the supported size.");
            var shader = Resources.Load<Shader>("G4AvatarMaterialBake");
            if (shader == null) throw new InvalidOperationException("Missing avatar material bake shader.");
            var bake = new Material(shader);
            bake.CopyPropertiesFromMaterial(source);
            bake.SetFloat("_BakeChannel", channel);
            Vector2 scale = source.mainTextureScale;
            bake.mainTextureScale = Vector2.Scale(scale, domain.size);
            bake.mainTextureOffset = source.mainTextureOffset + Vector2.Scale(scale, domain.position);
            var target = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32, channel == 1 ? RenderTextureReadWrite.sRGB : RenderTextureReadWrite.Linear);
            RenderTexture previous = RenderTexture.active;
            bool previousSrgb = GL.sRGBWrite;
            Texture2D result = null;
            try
            {
                GL.sRGBWrite = channel == 1 && QualitySettings.activeColorSpace == ColorSpace.Linear;
                Graphics.Blit(source.mainTexture != null ? source.mainTexture : Texture2D.whiteTexture, target, bake);
                RenderTexture.active = target;
                result = new Texture2D(width, height, TextureFormat.RGBA32, false, channel != 1);
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                result.Apply(false, false);
                return result;
            }
            catch { if (result != null) DestroyImmediate(result); throw; }
            finally
            {
                RenderTexture.active = previous;
                GL.sRGBWrite = previousSrgb;
                RenderTexture.ReleaseTemporary(target);
                DestroyImmediate(bake);
            }
        }
        private static void ApplyHeadAttachment(GameObject model, AvatarBoneTransform attachment)
        {
            if (model == null) return;
            model.transform.localPosition = attachment.Position;
            model.transform.localRotation = Quaternion.Euler(attachment.Rotation * Mathf.Rad2Deg);
            model.transform.localScale = attachment.Scale;
        }
        private static GameObject Add(Transform parent, string folder, string model, string texture, string textureFolder, bool fallbackTexture = false, System.Collections.Generic.IReadOnlyDictionary<uint, AvatarBoneTransform> boneTransforms = null, string skeletonModelHint = null, bool loadTextures = true)
        {
            if (string.IsNullOrEmpty(model)) return null;
            string modelPath = Path.Combine("chr", "_face", "20_EDIT", folder);
            string texturePath = Path.Combine("chr", "_face", "20_EDIT", textureFolder);
            try
            {
                GameObject part = G4ModelLoader.CreateModel(modelPath, model, texture, texturePath, fallbackTexture, true, boneTransforms, skeletonModelHint, loadTextures);
                part.transform.SetParent(parent, false);
                return part;
            }
            catch (Exception exception)
            {
                Transform orphan = parent.Find(model);
                if (orphan != null)
                {
                    if (Application.isPlaying) Destroy(orphan.gameObject);
                    else DestroyImmediate(orphan.gameObject);
                }
                Debug.LogWarning($"AvatarDemo failed to load {model}: {exception}");
                return null;
            }
        }

        private static Vector3 BoundsCenter(GameObject model)
        {
            if (model == null) return Vector3.zero;
            Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return Vector3.zero;
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return model.transform.parent.InverseTransformPoint(bounds.center);
        }


        private static Color RecipeColor(AvatarRecipeData data, int parameter, int firstSub = 0, int category = 5, bool lastMatch = false)
        {
            uint Channel(int sub, uint fallback)
            {
                uint value = data.Find(category, parameter, sub)?.RawValue ?? fallback;
                if (lastMatch) foreach (var field in data.Values)
                    if (field.Category == category && field.CategoryParam == parameter && field.CategoryParamSub == sub)
                        value = field.RawValue;
                return value;
            }
            float h = Channel(firstSub, 0) / 63f;
            float s = Channel(firstSub + 1, 0) / 63f;
            float v = Channel(firstSub + 2, 63) / 63f;
            Color color = Color.HSVToRGB(Mathf.Repeat(h, 1f), Mathf.Clamp01(s), Mathf.Clamp01(v));
            color.a = 1f;
            return color;
        }

        private static void ApplyColor(GameObject model, Color color, bool wholeSkin)
        {
            if (model == null) return;
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
            foreach (Material material in renderer.materials)
            {
                if (material.HasProperty("_SkinColor")) material.SetColor("_SkinColor", color);
                if (material.HasProperty("_WholeSkin")) material.SetFloat("_WholeSkin", wholeSkin ? 1f : 0f);
                if (!wholeSkin && material.HasProperty("_Color")) material.SetColor("_Color", color);
            }
        }

        private static System.Collections.Generic.List<G4TextureParser.TextureData> LoadHairDetailMaps()
        {
            string path = Path.Combine(DumpDataManager.Dx11Path, "chr", "_face", "20_EDIT", "_hairtex", "hair_10.g4tx");
            return G4TextureParser.Parse(File.ReadAllBytes(path), path, new System.Collections.Generic.List<string>(), default, false);
        }

        private static Texture2D CropHairDetail(G4TextureParser.TextureData map, int column, int row, RuntimeHeadResources owner, string model, bool linear = true)
        {
            if (map == null) throw new InvalidDataException("Missing edit hair detail texture.");
            int blockBytes = map.Format == TextureFormat.DXT1 ? 8 :
                map.Format == TextureFormat.DXT5 || map.Format == TextureFormat.BC7 ? 16 : 0;
            if (blockBytes == 0) throw new InvalidDataException("Unsupported hair detail format: " + map.Format);
            int width = map.Width / 16, height = map.Height / 32;
            var pixels = new System.Collections.Generic.List<byte>();
            int sourceOffset = 0, sourceWidth = map.Width, sourceHeight = map.Height, mips = 0;
            // Keep only mips whose tile boundaries remain aligned to compression
            // blocks. Lower atlas mips mix adjacent hairstyles into the same block.
            for (int w = width, h = height; mips < map.MipCount && w >= 4 && h >= 4; w /= 2, h /= 2)
            {
                int stride = ((sourceWidth + 3) / 4) * blockBytes;
                int x = column * w, y = row * h;
                if ((x | y | w | h) % 4 != 0) break;
                int bytes = (w / 4) * blockBytes;
                for (int line = 0; line < h / 4; line++)
                {
                    int offset = sourceOffset + (y / 4 + line) * stride + x / 4 * blockBytes;
                    for (int i = 0; i < bytes; i++) pixels.Add(map.Pixels[offset + i]);
                }
                sourceOffset += stride * ((sourceHeight + 3) / 4);
                sourceWidth = Math.Max(1, sourceWidth / 2); sourceHeight = Math.Max(1, sourceHeight / 2);
                mips++;
            }
            if (mips == 0) throw new InvalidDataException("Hair tile does not align to texture compression blocks.");
            var texture = owner.Own(new Texture2D(width, height, map.Format, mips, linear)
            {
                name = model + " " + map.Name + " standalone",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Trilinear,
                anisoLevel = 8
            });
            texture.LoadRawTextureData(pixels.ToArray());
            texture.Apply(false, true);
            return texture;
        }
        private static void ApplyHairBaseColor(GameObject model, Color hairColor, System.Collections.Generic.List<G4TextureParser.TextureData> details)
        {
            if (model == null) return;
            var resources = model.GetComponent<RuntimeHeadResources>();
            // Edit hair UVs address one tile of a 16-column, 32-row runtime atlas.
            // The loaded OC/SPM images contain just that tile. Preserve authored
            // margins by removing the tile origin, not normalizing mesh bounds.
            Vector2 minUV = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            Vector2 maxUV = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>())
            foreach (Vector2 uv in filter.sharedMesh.uv)
            {
                minUV = Vector2.Min(minUV, uv);
                maxUV = Vector2.Max(maxUV, uv);
            }
            Vector2 tile = (minUV + maxUV) * .5f;
            int column = Mathf.FloorToInt(tile.x * 16f), row = Mathf.FloorToInt(tile.y * 32f);
            foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>())
            {
                Vector2[] uv = filter.sharedMesh.uv;
                for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(uv[i].x * 16f - column, uv[i].y * 32f - row);
                filter.sharedMesh.uv = uv;
            }
            Texture2D texture = CropHairDetail(details.Find(map => map.Name == "hair_10"), column, row, resources, model.name, false);
            Texture2D oc = CropHairDetail(details.Find(map => map.Name == "hair_10oc"), column, row, resources, model.name);
            Texture2D spm = CropHairDetail(details.Find(map => map.Name == "hair_10spm"), column, row, resources, model.name);
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
            foreach (Material material in renderer.sharedMaterials)
            {
                material.mainTexture = texture;
                material.SetColor("_HairColor", hairColor);
                material.SetFloat("_RecolorWhite", 1f);
                // Edit hair OC packs illumination into G; R is also bright in shadows.
                material.SetFloat("_OcUseGreen", 1f);
                material.SetVector("_DetailUvST", new Vector4(1f, 1f, 0f, 0f));
                material.SetTexture("_OcTex", oc);
                material.SetTexture("_SpmTex", spm);
                material.SetColor("_Color", Color.white);
                material.SetColor("_SkinColor", Color.white);
                material.SetTexture("_MskTex", null);
                material.SetFloat("_HasMsk", 0f);
                material.SetFloat("_WholeSkin", 0f);
            }
        }
        private static void ApplyMaskedSkinColor(GameObject model, Color skinColor)
        {
            if (model == null) return;
            foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
            foreach (Material material in renderer.materials)
            {
                // Ear materials share face_10 and its mask/occlusion textures.
                // Recolor only the skin mask so the painted inner-ear shading is
                // retained. WholeSkin would flatten the complete ear to one color.
                if (material.HasProperty("_SkinColor")) material.SetColor("_SkinColor", skinColor);
                if (material.HasProperty("_WholeSkin")) material.SetFloat("_WholeSkin", 0f);
                if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
            }
        }
        private sealed class FaceLayer
        {
            public Texture2D Texture;
            public Texture2D Mask;
            public int Parameter;
            public bool Right;
            public string Resource;
        }

        private sealed class FacePose
        {
            public Vector2 Position;
            public float SharedHorizontalOffset;
            public Vector2 Scale = Vector2.one;
            public float Rotation;
        }

        private static void ApplyTextureAdjustment(GameObject head, FacePose pose, AvatarTextureTransform change, string meshName, bool sharedHorizontal = false)
        {
            // Highlight X is a common translation, not eye separation.
            pose.Position += new Vector2(sharedHorizontal ? 0f : change.Position.x, change.Position.y);
            if (sharedHorizontal) pose.SharedHorizontalOffset += change.Position.x;
            pose.Scale *= change.Scale;
            pose.Rotation += change.Rotation;
            if (Mathf.Abs(change.ModelHeight) < .000001f) return;
            // Convert model-space height to the mesh's actual UV span. The output
            // atlas has two rows and centered config coordinates span two units.
            Transform part = head.transform.Find(meshName);
            Mesh mesh = part == null ? null : part.GetComponent<MeshFilter>()?.sharedMesh;
            if (mesh == null) throw new InvalidOperationException("Missing facial mesh " + meshName);
            Vector3[] vertices = mesh.vertices;
            Vector2[] uv = mesh.uv;
            float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
            float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
            for (int i = 0; i < vertices.Length; i++)
            {
                minY = Mathf.Min(minY, vertices[i].y); maxY = Mathf.Max(maxY, vertices[i].y);
                minV = Mathf.Min(minV, uv[i].y); maxV = Mathf.Max(maxV, uv[i].y);
            }
            if (maxY - minY > .000001f)
                pose.Position.y += change.ModelHeight * (maxV - minV) / (maxY - minY) * 4f;
        }

        private static FacePose ReadFacePose(CfgTable table, string kind, string resource)
        {
            var pose = new FacePose();
            uint kindHash = G4TextureParser.Crc32(kind);
            uint resourceHash = string.IsNullOrEmpty(resource) ? 0u : G4TextureParser.Crc32(resource);
            uint baseScale = G4TextureParser.Crc32("TEX_PARTS_TYPE_BASE_SCALE_INFO");
            uint baseTrans = G4TextureParser.Crc32("TEX_PARTS_TYPE_BASE_TRANS_INFO");
            uint itemScale = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_SCALE_INFO");
            uint itemTrans = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_TRANS_INFO");
            foreach (CfgTable.Row row in table.Rows)
            {
                if (row.Values.Length < 3) continue;
                uint key = row.UInt(0);
                if (row.Hash == baseScale && key == kindHash || row.Hash == itemScale && key == resourceHash)
                    pose.Scale = Vector2.Scale(pose.Scale, new Vector2(Convert.ToSingle(row.Values[1]), Convert.ToSingle(row.Values[2])));
                if (row.Hash == baseTrans && key == kindHash)
                    pose.Position += new Vector2(Convert.ToSingle(row.Values[1]), Convert.ToSingle(row.Values[2]));
                // Item translations include a related-item count before X and Y.
                if (row.Hash == itemTrans && key == resourceHash && row.Values.Length >= 4)
                    pose.Position += new Vector2(Convert.ToSingle(row.Values[2]), Convert.ToSingle(row.Values[3]));
            }
            return pose;
        }

        private static void ApplyEyeRelation(CfgTable table, string eye, string kind, FacePose pose)
        {
            uint itemTrans = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_TRANS_INFO");
            uint relate = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_TRANS_RELATE");
            uint relateScale = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_TRANS_RELATE_SCALE");
            if (string.IsNullOrEmpty(eye)) return;
            uint eyeHash = G4TextureParser.Crc32(eye), kindHash = G4TextureParser.Crc32(kind);
            bool selectedEye = false, selectedKind = false;
            foreach (CfgTable.Row row in table.Rows)
            {
                if (row.Hash == itemTrans)
                {
                    selectedEye = row.Values.Length > 0 && row.UInt(0) == eyeHash;
                    selectedKind = false;
                }
                else if (selectedEye && row.Hash == relate && row.Values.Length >= 3)
                {
                    selectedKind = row.UInt(0) == kindHash;
                    if (selectedKind) pose.Position += new Vector2(Convert.ToSingle(row.Values[1]), Convert.ToSingle(row.Values[2]));
                }
                else if (selectedEye && selectedKind && row.Hash == relateScale && row.Values.Length >= 2)
                    pose.Scale = Vector2.Scale(pose.Scale, new Vector2(Convert.ToSingle(row.Values[0]), Convert.ToSingle(row.Values[1])));
            }
        }

        private static FacePose ParentPose(FacePose parent, FacePose child)
        {
            Vector2 offset = Vector2.Scale(parent.Scale, child.Position);
            float c = Mathf.Cos(parent.Rotation), s = Mathf.Sin(parent.Rotation);
            child.Position = parent.Position + new Vector2(c * offset.x - s * offset.y, s * offset.x + c * offset.y);
            child.SharedHorizontalOffset = parent.SharedHorizontalOffset + child.SharedHorizontalOffset * parent.Scale.x;
            child.Scale = Vector2.Scale(parent.Scale, child.Scale);
            child.Rotation += parent.Rotation;
            return child;
        }

        private static Vector4 TextureRotation(FacePose pose, bool right)
        {
            float angle = right ? -pose.Rotation : pose.Rotation;
            float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
            return new Vector4(c, -s, s, c);
        }

        private static Vector4 Placement(FacePose pose, bool right)
        {
            // Config translations use centered, upward-positive coordinates. DDS
            // rows and mesh UVs are native top-to-bottom, so invert the Y offset.
            return new Vector4(.5f + ((right ? -pose.Position.x : pose.Position.x) + pose.SharedHorizontalOffset) * .5f,
                .5f - pose.Position.y * .5f, pose.Scale.x, pose.Scale.y);
        }

        private static Texture2D ComposeFacialParts(GameObject owner, AvatarRecipeData data, AvatarHeadParts parts, int expression = 0, int mouthExpression = 4)
        {
            if (owner == null) return null;
            string posePath = Path.Combine(DumpDataManager.CommonPath, "chr", "_face", "20_EDIT", "texPartsDefaultPose.cfg.bin");
            CfgTable poseTable = CfgTable.Read(File.ReadAllBytes(posePath), posePath);
            var adjustments = AvatarRecipeParser.ResolveTextureTransforms(data);
            FacePose eyePose = ReadFacePose(poseTable, "eye", parts.EyeTexture);
            ApplyTextureAdjustment(owner, eyePose, adjustments[1], "parts_eye_10");
            FacePose pupilPose = ReadFacePose(poseTable, "pupil", parts.PupilTexture);
            ApplyEyeRelation(poseTable, parts.EyeTexture, "pupil", pupilPose);
            ApplyTextureAdjustment(owner, pupilPose, adjustments[2], "parts_eye_10");
            ParentPose(eyePose, pupilPose);
            FacePose highlightPose = ReadFacePose(poseTable, "highlight", parts.HighlightTexture);
            ApplyTextureAdjustment(owner, highlightPose, adjustments[3], "parts_eye_10", sharedHorizontal: true);
            ParentPose(pupilPose, highlightPose);
            FacePose eyebrowPose = ReadFacePose(poseTable, "eyebrow", parts.EyebrowTexture);
            ApplyEyeRelation(poseTable, parts.EyeTexture, "eyebrow", eyebrowPose);
            ApplyTextureAdjustment(owner, eyebrowPose, adjustments[4], "parts_eye_10");
            FacePose mouthPose = ReadFacePose(poseTable, "mouth", parts.MouthTexture);
            ApplyTextureAdjustment(owner, mouthPose, adjustments[5], "parts_mouth_10");
            var poses = new[] { eyePose, pupilPose, highlightPose, eyebrowPose, mouthPose };
            var layers = new System.Collections.Generic.List<FaceLayer>();
            // Feature 1 is the full-face foundation, below every expression layer.
            AddFaceLayers(layers, "00_face", parts.FaceTexture, 0);
            AddFaceLayers(layers, "01_eye", parts.EyeTexture, 1);
            AddFaceLayers(layers, "02_pupil", parts.PupilTexture, 2);
            AddFaceLayers(layers, "03_highlight", parts.HighlightTexture, 3);
            AddFaceLayers(layers, "04_eyebrow", parts.EyebrowTexture, 4);
            AddFaceLayers(layers, "05_mouth", parts.MouthTexture, 5);
            if (layers.Count == 0) return null;
            Shader shader = Resources.Load<Shader>("AvatarFaceComposite");
            if (shader == null || !shader.isSupported)
            {
                foreach (FaceLayer layer in layers) { Destroy(layer.Texture); if (layer.Mask != null) Destroy(layer.Mask); }
                throw new InvalidOperationException("AvatarFaceComposite shader is missing or unsupported.");
            }
            var material = new Material(shader);
            const int width = 2048, height = 1024;
            var a = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            var b = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
            RenderTexture initial = RenderTexture.active;
            RenderTexture.active = a;
            Color skin = RecipeColor(data, 0);
            Color empty = QualitySettings.activeColorSpace == ColorSpace.Linear ? skin.linear : skin;
            empty.a = 0f;
            GL.Clear(true, true, empty);
            RenderTexture.active = initial;
            try
            {
                for (int i = 0; i < layers.Count; i++)
                {
                    FaceLayer layer = layers[i];
                    FacePose pose = layer.Parameter == 0 ? new FacePose() : poses[layer.Parameter - 1];
                    material.SetTexture("_LayerTex", layer.Texture);
                    material.SetTexture("_LayerMask", layer.Mask);
                    material.SetFloat("_HasLayerMask", layer.Mask != null ? 1f : 0f);
                    material.SetVector("_LayerPlacement", Placement(pose, layer.Right));
                    material.SetVector("_LayerRotation", TextureRotation(pose, layer.Right));
                    // Pupil and highlight are single images, not expression atlases.
                    material.SetVector("_SourceGrid", layer.Parameter == 0 || layer.Parameter == 2 || layer.Parameter == 3
                        ? new Vector4(1f, 1f, 0f, 0f) : new Vector4(4f, 2f, 0f, 0f));
                    material.SetVector("_AtlasGrid", new Vector4(4f, 2f, 0f, 0f));
                    int selectedExpression = Mathf.Clamp(layer.Parameter == 5 ? mouthExpression : expression, 0, 7);
                    material.SetVector("_ExpressionOffset", new Vector4(selectedExpression % 4, selectedExpression / 4, 0f, 0f));
                    material.SetFloat("_LayerKind", layer.Parameter);
                    material.SetColor("_SkinColor", QualitySettings.activeColorSpace == ColorSpace.Linear ? skin.linear : skin);
                    Color primary = layer.Parameter == 0 ? RecipeColor(data, 0, lastMatch: true) :
                        layer.Parameter == 1 ? RecipeColor(data, 1, 3) :
                        layer.Parameter == 2 ? RecipeColor(data, 2, layer.Right ? 9 : 6) :
                        layer.Parameter == 4 ? RecipeColor(data, 4) :
                        layer.Parameter == 5 ? RecipeColor(data, 0) : Color.white;
                    Color secondary = layer.Parameter == 0 ? primary : RecipeColor(data, 2, layer.Right ? 3 : 0);
                    material.SetColor("_PrimaryColor", QualitySettings.activeColorSpace == ColorSpace.Linear ? primary.linear : primary);
                    material.SetColor("_SecondaryColor", QualitySettings.activeColorSpace == ColorSpace.Linear ? secondary.linear : secondary);
                    FaceLayer eye = layers.Find(value => value.Parameter == 1 && value.Right == layer.Right);
                    material.SetTexture("_EyeMask", eye == null ? null : eye.Mask);
                    material.SetFloat("_HasEyeMask", eye != null && eye.Mask != null ? 1f : 0f);
                    material.SetVector("_EyePlacement", Placement(eyePose, layer.Right));
                    material.SetVector("_EyeRotation", TextureRotation(eyePose, layer.Right));
                    Graphics.Blit(a, b, material);
                    RenderTexture swap = a; a = b; b = swap;
                    if (layer.Parameter == 0 && (i + 1 == layers.Count || layers[i + 1].Parameter != 0))
                    {
                        ApplyFeatureBase(owner, a, width / 4, height / 2);
                        // Keep the static feature on the head. Transparent expression
                        // backgrounds let it show through without moving with SP nodes.
                        RenderTexture previous = RenderTexture.active;
                        RenderTexture.active = a;
                        GL.Clear(true, true, empty);
                        RenderTexture.active = previous;
                    }
                }
                RenderTexture old = RenderTexture.active; RenderTexture.active = a;
                var result = new Texture2D(width, height, TextureFormat.RGBA32, false)
                { name = "AvatarRecipeFacialParts", wrapMode = TextureWrapMode.Clamp };
                result.ReadPixels(new Rect(0, 0, width, height), 0, 0); result.Apply(false, false);
                RenderTexture.active = old;
                owner.GetComponent<RuntimeHeadResources>().Own(result);
                return result;
            }
            finally
            {
                foreach (FaceLayer layer in layers)
                {
                    if (layer.Texture != null) Destroy(layer.Texture);
                    if (layer.Mask != null) Destroy(layer.Mask);
                }
                Destroy(material); RenderTexture.ReleaseTemporary(a); RenderTexture.ReleaseTemporary(b);
            }
        }

        private static void ApplyFeatureBase(GameObject head, RenderTexture atlas, int width, int height)
        {
            Transform part = head.transform.Find("base_eye_10");
            if (part == null) throw new InvalidOperationException("Missing face base mesh.");
            // The head uses ordinary face UVs; expression meshes use the 4x2 atlas.
            // Keep one identical feature cell for the head so their seams agree.
            RenderTexture previous = RenderTexture.active;
            RenderTexture.active = atlas;
            var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
            { name = "AvatarRecipeFeatureBase", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
            try { texture.ReadPixels(new Rect(0, 0, width, height), 0, 0); texture.Apply(false, false); }
            finally { RenderTexture.active = previous; }
            head.GetComponent<RuntimeHeadResources>().Own(texture);
            foreach (Material material in part.GetComponent<Renderer>().sharedMaterials)
            {
                material.mainTexture = texture;
                material.SetColor("_Color", Color.white);
                material.SetFloat("_WholeSkin", 0f);
                material.SetFloat("_HasMsk", 0f);
            }
        }
        private static void AddFaceLayers(System.Collections.Generic.List<FaceLayer> output, string folder, string name, int parameter)
        {
            if (string.IsNullOrEmpty(name)) return;
            try
            {
                string path = Path.Combine(DumpDataManager.Dx11Path, "chr", "_face", "20_EDIT", "_facetex", folder, name + ".g4tx");
                var warnings = new System.Collections.Generic.List<string>();
                var maps = G4TextureParser.Parse(File.ReadAllBytes(path), path, warnings, default, false);
                foreach (G4TextureParser.TextureData map in maps)
                {
                    if (!G4TextureParser.IsBase(map.Name)) continue;
                    G4TextureParser.TextureData maskMap = maps.Find(value =>
                        string.Equals(value.Name, map.Name + "msk", StringComparison.OrdinalIgnoreCase));
                    output.Add(new FaceLayer
                    {
                        Texture = CreateFaceTexture(map),
                        Mask = maskMap == null ? null : CreateFaceTexture(maskMap, true),
                        Parameter = parameter,
                        Resource = name,
                        Right = map.Name.IndexOf("_R_", StringComparison.OrdinalIgnoreCase) >= 0
                    });
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning($"AvatarDemo failed to load facial texture {name}: {exception.Message}");
            }
        }

        private static Texture2D CreateFaceTexture(G4TextureParser.TextureData map, bool linear = false)
        {
            var texture = new Texture2D(map.Width, map.Height, map.Format, map.MipCount, linear)
            {
                name = map.Name,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            texture.LoadRawTextureData(map.Pixels);
            texture.Apply(false, false);
            return texture;
        }

        private static void ApplyFacialPartsTexture(GameObject head, Texture2D texture)
        {
            if (head == null || texture == null) return;
            foreach (Renderer renderer in head.GetComponentsInChildren<Renderer>())
            {
                if (!renderer.name.StartsWith("parts_eye_", StringComparison.OrdinalIgnoreCase) &&
                    !renderer.name.StartsWith("parts_mouth_", StringComparison.OrdinalIgnoreCase)) continue;
                foreach (Material material in renderer.materials)
                {
                    material.mainTexture = texture;
                    if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
                    if (material.HasProperty("_WholeSkin")) material.SetFloat("_WholeSkin", 0f);
                    if (material.HasProperty("_HasMsk")) material.SetFloat("_HasMsk", 0f);
                    if (material.HasProperty("_OcDarkness")) material.SetFloat("_OcDarkness", 1f);
                    if (material.HasProperty("_SpmStrength")) material.SetFloat("_SpmStrength", 0f);
                    float depthBias = renderer.name.StartsWith("parts_mouth_", StringComparison.OrdinalIgnoreCase) ? -2f : -1f;
                    if (material.HasProperty("_DepthBiasFactor")) material.SetFloat("_DepthBiasFactor", depthBias);
                    if (material.HasProperty("_DepthBiasUnits")) material.SetFloat("_DepthBiasUnits", depthBias);
                }
            }
        }
    }
}
