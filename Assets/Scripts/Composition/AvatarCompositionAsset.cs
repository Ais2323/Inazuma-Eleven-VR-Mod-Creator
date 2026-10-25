using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using G4Runtime;
using UnityEngine;

namespace G4Composition
{
    // Generate a recipe on Unity's main thread, then adapt its meshes and BC7 maps
    // to native character slots. No generated files are read from the source dump.
    public static class AvatarCompositionAsset
    {
        public static string SourceId(string code) => "Avatar_" + RawHeadAsset.Digest(Encoding.UTF8.GetBytes(code)).Substring(0, 20);

        public static RawHeadAsset Build(string code)
        {
            var recipe = AvatarRecipeParser.Parse(code);
            string id = SourceId(recipe.Code);
            var asset = RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path,
                Path.Combine("01_IE1", "c01000010", "c01000010.g4md"), CancellationToken.None);
            asset.RelativePath = "Avatar/" + id + ".g4md";
            GameObject model = AvatarHeadBuilder.Build(recipe);
            try
            {
                uint head = G4CompositionWriter.TargetHeadJoint(asset);
                foreach (var part in asset.Preview.Parts)
                {
                    string name = part.Name.StartsWith("eye_", StringComparison.OrdinalIgnoreCase) ? "eye_10" :
                        part.Name.StartsWith("mouth_", StringComparison.OrdinalIgnoreCase) ? "mouth_10" : "hair_10";
                    Transform child = model.transform.Find(name);
                    if (child == null) throw new InvalidDataException("Avatar 缺少 " + name);
                    Mesh mesh = child.GetComponent<MeshFilter>().sharedMesh;
                    Matrix4x4 toRoot = model.transform.worldToLocalMatrix * child.localToWorldMatrix;
                    if (name == "hair_10") asset.LegacyHairAtlasBounds = child.GetComponent<AvatarMeshSkin>().LegacyAtlasBounds;
                    var raw = asset.Meshes[part.SourceMeshIndex];
                    EncodeMesh(raw, asset.Layouts[raw.Layout], mesh, toRoot, head, child.GetComponent<AvatarMeshSkin>());
                    part.Name = name;
                    part.Positions = mesh.vertices.Select(toRoot.MultiplyPoint3x4).ToArray();
                    part.Normals = mesh.normals;
                    part.UV = mesh.uv;
                    part.Colors = mesh.colors32;
                    part.Indices = mesh.triangles;
                    var material = child.GetComponent<Renderer>().sharedMaterial;
                    var hashes = asset.Preview.Materials[raw.Material].TextureHashes;
                    foreach (var texture in asset.Textures.Where(t => hashes.Contains(t.Hash)))
                    {
                        var match = Regex.Match(texture.Name, @"_\d{2}(oc|spm)?$", RegexOptions.IgnoreCase);
                        if (!match.Success) continue;
                        string channel = match.Groups[1].Value.ToLowerInvariant();
                        Texture2D generated = material.GetTexture(channel == "oc" ? "_OcTex" : channel == "spm" ? "_SpmTex" : "_MainTex") as Texture2D;
                        if (generated != null) ReplacePayload(texture, generated);
                        else if (channel == "oc") SetConstant(texture, Color.white);
                        else if (channel == "spm") SetConstant(texture, Color.black);
                    }
                }
                asset.TextureContainer = CharacterMakerBatchExporter.WriteG4TxPreservingTable(asset.TextureContainer, asset.Textures);
                return asset;
            }
            finally
            {
                model.SetActive(false);
                if (Application.isPlaying) UnityEngine.Object.Destroy(model); else UnityEngine.Object.DestroyImmediate(model);
            }
        }

        private static void EncodeMesh(RawMesh raw, byte[] layout, Mesh mesh, Matrix4x4 transform, uint head, AvatarMeshSkin skin)
        {
            if (mesh.vertexCount > ushort.MaxValue) throw new InvalidDataException("Avatar 網格超過原生 16-bit 頂點限制。");
            if (skin != null)
            {
                if (skin.Hashes.Length != mesh.vertexCount * 8 || skin.Weights.Length != skin.Hashes.Length)
                    throw new InvalidDataException("Avatar skin/vertex count mismatch.");
                raw.Palette = skin.Hashes.Where((h, i) => skin.Weights[i] != 0).Append(head).Distinct().ToArray();
                if (raw.Palette.Length > 255) throw new InvalidDataException("Avatar joint palette capacity exceeded.");
                raw.PreserveSkinning = true;
            }
            int bone = Array.IndexOf(raw.Palette, head);
            if (bone < 0) throw new InvalidDataException("原生 Avatar 基底缺少頭部關節。");
            var elements = new Dictionary<int, (int offset, int format)>();
            for (int i = 0; i < layout[1]; i++)
            {
                int e = 8 + i * 8;
                elements[layout[e]] = (BitConverter.ToUInt16(layout, e + 1), BitConverter.ToInt32(layout, e + 4));
            }
            var vertices = mesh.vertices; var normals = mesh.normals; var uv = mesh.uv; var colors = mesh.colors32;
            byte[] bytes = new byte[checked(mesh.vertexCount * raw.Stride)];
            for (int i = 0; i < mesh.vertexCount; i++)
            {
                int start = i * raw.Stride;
                Buffer.BlockCopy(raw.Vertices, 0, bytes, start, raw.Stride);
                Vector3 p = transform.MultiplyPoint3x4(vertices[i]);
                int pos = start + elements[1].offset;
                Put(bytes, pos, p.x); Put(bytes, pos + 4, p.y); Put(bytes, pos + 8, -p.z);
                if (elements.TryGetValue(2, out var normal))
                {
                    Vector3 n = transform.inverse.transpose.MultiplyVector(normals[i]).normalized;
                    Put(bytes, start + normal.offset, Snorm(n.x)); Put(bytes, start + normal.offset + 2, Snorm(n.y)); Put(bytes, start + normal.offset + 4, Snorm(-n.z));
                }
                var tex = elements[10]; int t = start + tex.offset;
                if (tex.format == 2 || tex.format == 3) { Put(bytes, t, uv[i].x); Put(bytes, t + 4, uv[i].y); }
                else if (tex.format == 18 || tex.format == 20) { Put(bytes, t, Snorm(uv[i].x)); Put(bytes, t + 2, Snorm(uv[i].y)); }
                else if (tex.format == 14) { Put(bytes, t, (ushort)Mathf.RoundToInt(Mathf.Clamp01(uv[i].x) * 65535)); Put(bytes, t + 2, (ushort)Mathf.RoundToInt(Mathf.Clamp01(uv[i].y) * 65535)); }
                else if (new[] {0,1,10,11,13,16,17}.Contains(tex.format)) { Put(bytes,t,Mathf.FloatToHalf(uv[i].x));Put(bytes,t+2,Mathf.FloatToHalf(uv[i].y)); }
                else throw new InvalidDataException("不支援的 Avatar UV 格式：" + tex.format);
                if (elements.TryGetValue(8, out var color))
                {
                    Color32 c = colors.Length == vertices.Length ? colors[i] : new Color32(255,255,255,255);
                    int at = start + color.offset; bytes[at] = c.r; bytes[at+1] = c.g; bytes[at+2] = c.b; bytes[at+3] = c.a;
                }
                int weights = start + elements[5].offset, bones = start + elements[6].offset;
                Array.Clear(bytes, weights, 16); Array.Clear(bytes, bones, 8);
                if (skin == null) { Put(bytes, weights, ushort.MaxValue); bytes[bones] = checked((byte)bone); }
                else for (int j = 0; j < 8; j++)
                {
                    int at = i * 8 + j;
                    ushort weight = skin.Weights[at];
                    if (weight == 0) continue;
                    Put(bytes, weights + j * 2, weight);
                    bytes[bones + j] = checked((byte)Array.IndexOf(raw.Palette, skin.Hashes[at]));
                }
            }
            int[] indices = mesh.triangles;
            var native = new ushort[indices.Length];
            for (int i = 0; i < indices.Length; i += 3) { native[i] = checked((ushort)indices[i]); native[i+1] = checked((ushort)indices[i+2]); native[i+2] = checked((ushort)indices[i+1]); }
            raw.Vertices = bytes; raw.VertexCount = mesh.vertexCount; raw.Indices = native;
        }
        private static ushort Snorm(float value) => unchecked((ushort)(short)Mathf.RoundToInt(Mathf.Clamp(value,-1,1)*32767));
        private static void Put(byte[] bytes,int at,float value) => Buffer.BlockCopy(BitConverter.GetBytes(value),0,bytes,at,4);
        private static void Put(byte[] bytes,int at,ushort value) => Buffer.BlockCopy(BitConverter.GetBytes(value),0,bytes,at,2);

        private static void ReplacePayload(RawTexture slot, Texture2D texture)
        {
            var expected = G4TextureParser.ParseDDS(slot.Dds, slot.Name);
            byte[] data = texture.GetRawTextureData();
            if (expected.Width != texture.width || expected.Height != texture.height || expected.Format != texture.format || expected.MipCount != texture.mipmapCount || data.Length != slot.Dds.Length - 148)
                throw new InvalidDataException("Avatar 貼圖不符合原生槽位：" + slot.Name);
            var dds = (byte[])slot.Dds.Clone();
            Buffer.BlockCopy(data,0,dds,148,data.Length);
            slot.Dds = dds;
        }
        private static void SetConstant(RawTexture slot, Color32 color)
        {
            var expected = G4TextureParser.ParseDDS(slot.Dds, slot.Name);
            byte[] dds = (byte[])slot.Dds.Clone(); int at = 148;
            for (int mip = 0; mip < expected.MipCount; mip++)
            {
                int width = Math.Max(1,expected.Width >> mip), height = Math.Max(1,expected.Height >> mip);
                var pixels = Enumerable.Repeat(color,width*height).ToArray();
                byte[] encoded = Bc7PortraitEncoder.Encode(pixels,width,height,default);
                Buffer.BlockCopy(encoded,148,dds,at,encoded.Length-148); at += encoded.Length-148;
            }
            if (at != dds.Length) throw new InvalidDataException("Avatar 中性貼圖大小不符。");
            slot.Dds = dds;
        }
    }
}