using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using G4Runtime;

namespace G4Composition
{
    public static class CompositionPortrait
    {
        public static byte[] Build(G4CompositionWriter.Result model, string templatePath, string sourceStem, string destinationStem, CancellationToken token)
        {
            byte[] template = File.ReadAllBytes(templatePath); var layers = RawTexture.Read(template, templatePath);
            if (layers.Count != 2) throw new InvalidDataException("Portrait template must contain two DDS layers.");
            int width = BitConverter.ToUInt16(layers[0].Descriptor, 24), height = BitConverter.ToUInt16(layers[0].Descriptor, 26);
            if (width < 4 || height < 4 || width > 2048 || height > 2048 || layers.Any(t => BitConverter.ToUInt16(t.Descriptor, 24) != width || BitConverter.ToUInt16(t.Descriptor, 26) != height || !t.Name.Contains(sourceStem))) throw new InvalidDataException("Unsupported native portrait template.");
            var parsed = G4ModelParser.Parse(model.Metadata, model.Geometry, "portrait source", token);
            var warnings = new List<string>(); var maps = G4TextureParser.Parse(model.Textures, "portrait textures", warnings, token);
            var objects = new List<UnityEngine.Object>(); GameObject root = null; RenderTexture previous = RenderTexture.active;
            try
            {
                root = new GameObject("Portrait render"); root.hideFlags = HideFlags.HideAndDontSave;
                var materials = new Material[parsed.Materials.Count]; var textures = new Dictionary<G4TextureParser.TextureData, Texture2D>();
                for (int i = 0; i < materials.Length; i++)
                {
                    var data = G4TextureParser.Choose(parsed.Materials[i], maps) ?? throw new InvalidDataException("Portrait material lacks a base texture.");
                    if (!textures.TryGetValue(data, out var texture))
                    {
                        texture = new Texture2D(data.Width, data.Height, data.Format, data.MipCount, false); objects.Add(texture);
                        texture.LoadRawTextureData(data.Pixels); texture.Apply(false); textures.Add(data, texture);
                    }
                    var mat = new Material(Resources.Load<Shader>("G4HeadPreview")); mat.mainTexture = texture; materials[i] = mat; objects.Add(mat);
                }
                Bounds bounds = default; bool first = true;
                foreach (var part in parsed.Parts)
                {
                    var mesh = new Mesh { vertices = part.Positions, normals = part.Normals, uv = part.UV, colors32 = part.Colors, triangles = part.Indices }; mesh.RecalculateBounds(); objects.Add(mesh);
                    var go = new GameObject(part.Name, typeof(MeshFilter), typeof(MeshRenderer)); go.layer = 31; go.transform.SetParent(root.transform, false); go.GetComponent<MeshFilter>().sharedMesh = mesh; go.GetComponent<MeshRenderer>().sharedMaterial = materials[part.Material];
                    if (first) { bounds = mesh.bounds; first = false; } else bounds.Encapsulate(mesh.bounds);
                }
                var cameraObject = new GameObject("Portrait camera", typeof(Camera)); cameraObject.transform.SetParent(root.transform, false);
                var camera = cameraObject.GetComponent<Camera>(); camera.enabled = false; camera.cullingMask = 1 << 31; camera.orthographic = true;
                camera.orthographicSize = Mathf.Max(bounds.extents.y, bounds.extents.x * height / width) * 1.08f;
                float radius = Mathf.Max(bounds.extents.magnitude, .01f); camera.transform.position = bounds.center + new Vector3(0, 0, -radius * 3); camera.transform.rotation = Quaternion.identity;
                camera.nearClipPlane = radius * .01f; camera.farClipPlane = radius * 6; camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = Color.clear;
                var render = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32); objects.Add(render); render.Create(); camera.targetTexture = render;
                camera.Render(); RenderTexture.active = render;
                var capture = new Texture2D(width, height, TextureFormat.RGBA32, false); objects.Add(capture); capture.ReadPixels(new Rect(0, 0, width, height), 0, 0); capture.Apply();
                Color32[] pixels = capture.GetPixels32(); var native = new Color32[pixels.Length];
                for (int y = 0; y < height; y++) Array.Copy(pixels, y * width, native, (height - 1 - y) * width, width);
                byte[] dds = Bc7PortraitEncoder.Encode(native, width, height, token);
                foreach (var layer in layers) { layer.Name = layer.Name.Replace(sourceStem, destinationStem); layer.Hash = G4TextureParser.Crc32(layer.Name); layer.Dds = dds; }
                // Both native portrait layers use the assembled head; v1 has no automatic rear-hair classification.
                return RawTexture.Write(template, layers);
            }
            finally
            {
                RenderTexture.active = previous;
                if (root != null) { root.SetActive(false); if (Application.isPlaying) UnityEngine.Object.Destroy(root); else UnityEngine.Object.DestroyImmediate(root); }
                foreach (var obj in objects) { if (obj is RenderTexture rt) rt.Release(); if (Application.isPlaying) UnityEngine.Object.Destroy(obj); else UnityEngine.Object.DestroyImmediate(obj); }
            }
        }
    }
    public static class Bc7PortraitEncoder
    {
        // BC7 mode 6, per Microsoft BC7 format specification:
        // https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format
        static readonly int[] Weights = { 0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64 };
        static int Channel(Color32 c, int channel) => channel == 0 ? c.r : channel == 1 ? c.g : channel == 2 ? c.b : c.a;
        public static byte[] Encode(Color32[] pixels, int width, int height, CancellationToken token)
        {
            if (pixels.Length != checked(width * height)) throw new ArgumentException("Portrait pixel dimensions mismatch.");
            using (var stream = new MemoryStream()) using (var writer = new BinaryWriter(stream))
            {
                writer.Write(System.Text.Encoding.ASCII.GetBytes("DDS ")); writer.Write(124); writer.Write(0x81007); writer.Write(height); writer.Write(width); writer.Write(((width + 3) / 4) * ((height + 3) / 4) * 16); writer.Write(0); writer.Write(1); writer.Write(new byte[44]);
                writer.Write(32); writer.Write(4); writer.Write(System.Text.Encoding.ASCII.GetBytes("DX10")); writer.Write(new byte[20]); writer.Write(0x1000); writer.Write(new byte[16]);
                writer.Write(98); writer.Write(3); writer.Write(0); writer.Write(1); writer.Write(0);
                for (int by = 0; by < height; by += 4)
                    for (int bx = 0; bx < width; bx += 4)
                    {
                        token.ThrowIfCancellationRequested(); var block = new Color32[16];
                        for (int i = 0; i < 16; i++) block[i] = pixels[Math.Min(height - 1, by + i / 4) * width + Math.Min(width - 1, bx + i % 4)];
                        writer.Write(Block(block));
                    }
                return stream.ToArray();
            }
        }
        static byte[] Block(Color32[] pixels)
        {
            // Choose the most distant actual colors, retaining correlated RGB/alpha endpoints.
            int first = 0, last = 0, max = -1;
            for (int a = 0; a < 16; a++) for (int b = a; b < 16; b++)
            {
                int error = 0; for (int c = 0; c < 4; c++) { int d = Channel(pixels[a], c) - Channel(pixels[b], c); error += d * d; }
                if (error > max) { max = error; first = a; last = b; }
            }
            var endpoints = new int[2, 4]; var parity = new int[2];
            for (int e = 0; e < 2; e++)
            {
                int best = int.MaxValue;
                for (int p = 0; p < 2; p++)
                {
                    int error = 0; var quantized = new int[4];
                    for (int c = 0; c < 4; c++) { int original = Channel(pixels[e == 0 ? first : last], c); int q = Math.Max(0, Math.Min(127, (int)Math.Round((original - p) / 2.0))); quantized[c] = q * 2 + p; int delta = original - quantized[c]; error += delta * delta; }
                    if (error < best) { best = error; parity[e] = p; for (int c = 0; c < 4; c++) endpoints[e, c] = quantized[c]; }
                }
            }
            var indices = new int[16];
            for (int i = 0; i < 16; i++)
            {
                int best = int.MaxValue;
                for (int index = 0; index < 16; index++)
                {
                    int error = 0; for (int c = 0; c < 4; c++) { int value = ((64 - Weights[index]) * endpoints[0, c] + Weights[index] * endpoints[1, c] + 32) >> 6; int delta = Channel(pixels[i], c) - value; error += delta * delta; }
                    if (error < best) { best = error; indices[i] = index; }
                }
            }
            if (indices[0] >= 8)
            {
                for (int c = 0; c < 4; c++) { int t = endpoints[0, c]; endpoints[0, c] = endpoints[1, c]; endpoints[1, c] = t; }
                int p = parity[0]; parity[0] = parity[1]; parity[1] = p;
                for (int i = 0; i < 16; i++) indices[i] = 15 - indices[i];
            }
            var result = new byte[16]; int bit = 0;
            Action<int, int> put = (value, count) => { for (int i = 0; i < count; i++, bit++) result[bit / 8] |= (byte)(((value >> i) & 1) << (bit % 8)); };
            put(64, 7); for (int c = 0; c < 4; c++) for (int e = 0; e < 2; e++) put(endpoints[e, c] >> 1, 7);
            put(parity[0], 1); put(parity[1], 1); for (int i = 0; i < 16; i++) put(indices[i], i == 0 ? 3 : 4);
            if (bit != 128) throw new InvalidDataException("Invalid BC7 block length.");
            return result;
        }
    }
}
