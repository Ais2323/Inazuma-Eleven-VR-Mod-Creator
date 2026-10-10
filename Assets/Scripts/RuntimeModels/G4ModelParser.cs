using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using UnityEngine;

namespace G4Runtime
{
    // Binary contracts ported from G4_Blender-main/g4_model_probe.py.
    // This class only creates managed data and can run off the Unity main thread.
    public static class G4ModelParser
    {
        public sealed class Model
        {
            public readonly List<Part> Parts = new List<Part>();
            public readonly List<Surface> Materials = new List<Surface>();
            public readonly List<string> Warnings = new List<string>();
        }
        public sealed class Part
        {
            public int SourceMeshIndex;
            public string Name;
            public int Material;
            public Vector3[] Positions, Normals;
            public Vector2[] UV;
            public Color32[] Colors;
            public int[] Indices;
        }
        public sealed class Surface
        {
            public string Name;
            public readonly List<uint> TextureHashes = new List<uint>();
        }
        internal sealed class Binary
        {
            public readonly byte[] Data;
            readonly string name;
            public Binary(byte[] data, string name) { Data = data; this.name = name; }
            public void Range(long offset, long size)
            {
                if (offset < 0 || size < 0 || offset > Data.Length - size)
                    throw new InvalidDataException($"{name}: invalid range 0x{offset:X}, length {size} (file size {Data.Length}).");
            }
            public byte U8(int o) { Range(o, 1); return Data[o]; }
            public ushort U16(int o) { Range(o, 2); return BitConverter.ToUInt16(Data, o); }
            public short I16(int o) { Range(o, 2); return BitConverter.ToInt16(Data, o); }
            public uint U32(int o) { Range(o, 4); return BitConverter.ToUInt32(Data, o); }
            public int Int(int o) { uint v = U32(o); if (v > int.MaxValue) throw new InvalidDataException(name + ": oversized field."); return (int)v; }
            public float F32(int o)
            {
                Range(o, 4); float v = BitConverter.ToSingle(Data, o);
                if (float.IsNaN(v) || float.IsInfinity(v)) throw new InvalidDataException(name + ": non-finite vertex data.");
                return v;
            }
            public string Text(int o)
            {
                Range(o, 1); int end = o;
                while (end < Data.Length && Data[end] != 0 && end - o < 4096) end++;
                if (end == Data.Length || end - o == 4096) throw new InvalidDataException(name + ": unterminated string.");
                return Encoding.ASCII.GetString(Data, o, end - o);
            }
            public void Magic(string value) { Range(0, value.Length); if (Encoding.ASCII.GetString(Data, 0, value.Length) != value) throw new InvalidDataException(name + ": expected " + value); }
        }
        struct Element { public int Offset, Format; }
        static int Align16(int value) => checked((value + 15) & ~15);
        static string[] Names(Binary b, int table, int count)
        {
            b.Range(table, count * 2L);
            var names = new string[count];
            for (int i = 0; i < count; i++) names[i] = b.Text(checked(table + b.U16(table + i * 2)));
            return names;
        }
        public static Model Parse(byte[] metadata, byte[] geometry, string source, CancellationToken token = default)
        {
            var md = new Binary(metadata, source); var mg = new Binary(geometry, Path.ChangeExtension(source, ".g4mg"));
            md.Magic("G4MD"); md.Range(0, 0x88);
            int table = md.U16(4), count = md.U16(0x20), materialCount = md.U16(0x22), bias = md.U16(0x0A) * 4;
            md.Range(table, count * 0x50L);
            int vertexBytes = md.Int(0x50), indexBytes = md.Int(0x54), indexBase = md.Int(0x5C);
            mg.Range(0, vertexBytes); mg.Range(indexBase, indexBytes);
            int cursor = checked(table + count * 0x50);
            var layouts = new List<Dictionary<int, Element>>();
            for (int i = 0; i < md.U8(0x26); i++)
            {
                md.Range(cursor, 8); int entries = md.U8(cursor + 1);
                md.Range(cursor + 8, entries * 8L);
                var layout = new Dictionary<int, Element>();
                for (int j = 0; j < entries; j++)
                {
                    int o = cursor + 8 + j * 8;
                    layout[md.U8(o)] = new Element { Offset = md.U16(o + 1), Format = md.Int(o + 4) };
                }
                layouts.Add(layout); cursor += 8 + entries * 8;
            }
            int materialTable = Align16(cursor);
            md.Range(materialTable, materialCount * 16L);
            string[] meshNames = Names(md, bias + md.U16(0x84) * 4, count);
            string[] materialNames = Names(md, bias + md.U16(0x86) * 4, materialCount);
            int hashTable = bias + md.U16(0x76) * 4, hashCount = md.U8(0x27);
            md.Range(hashTable, hashCount * 4L);
            bool explicitRefs = bias + md.U16(0x64) * 4 == materialTable;
            int refTable = bias + md.U16(0x68) * 4, refEnd = bias + md.U16(0x6A) * 4;
            var model = new Model();
            if (explicitRefs) md.Range(refTable, refEnd - (long)refTable);
            else model.Warnings.Add(source + ": legacy material descriptor layout; using texture names.");
            for (int i = 0; i < materialCount; i++)
            {
                var surface = new Surface { Name = materialNames[i] };
                int o = materialTable + i * 16;
                if (explicitRefs)
                {
                    int start = md.U16(o + 6), refs = md.U8(o + 12);
                    if ((start + (long)refs) * 6 > refEnd - refTable) throw new InvalidDataException(source + ": material references outside descriptor table.");
                    for (int j = 0; j < refs; j++)
                    {
                        int slot = md.U8(refTable + (start + j) * 6);
                        if (slot < hashCount) surface.TextureHashes.Add(md.U32(hashTable + slot * 4));
                    }
                }
                model.Materials.Add(surface);
            }
            var lods = meshNames.Select(n => Regex.Match(n, @"^(.*?)(?:_LOD(\d+))?$", RegexOptions.IgnoreCase)).ToArray();
            var bestLod = lods.GroupBy(m => m.Groups[1].Value, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Min(m => m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0), StringComparer.OrdinalIgnoreCase);
            // Combo files live in _face but also embed both actors' uniforms, shoes and ball.
            // Keep their head records only; preserve both heads in their authored positions.
            bool combo = source.Replace('\\', '/').IndexOf("/22_COMBO/", StringComparison.OrdinalIgnoreCase) >= 0;
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                if (combo && !Regex.IsMatch(meshNames[i], @"^(?:tw_)?(?:c\d|eye_|mouth_|hair|face)", RegexOptions.IgnoreCase)) continue;
                var lod = lods[i]; int level = lod.Groups[2].Success ? int.Parse(lod.Groups[2].Value) : 0;
                if (level != bestLod[lod.Groups[1].Value]) continue;
                int o = table + i * 0x50, vo = md.Int(o), io = md.Int(o + 4), vc = md.Int(o + 8), ic = md.Int(o + 12);
                int stride = md.U16(o + 0x3E) & 255; if (stride == 0) stride = 0x44;
                if (vc == 0 || ic == 0) continue;
                if (ic % 3 != 0 || stride < 12 || vo + (long)vc * stride > vertexBytes || io + ic * 2L > indexBytes)
                    throw new InvalidDataException($"{source}: invalid geometry in {meshNames[i]}.");
                int layoutIndex = md.U8(o + 0x42), mat = md.U8(o + 0x43);
                if (layoutIndex >= layouts.Count || mat >= materialCount) throw new InvalidDataException(source + ": invalid layout/material index.");
                var layout = layouts[layoutIndex];
                var part = new Part { SourceMeshIndex = i, Name = meshNames[i], Material = mat, Positions = new Vector3[vc], Normals = new Vector3[vc], UV = new Vector2[vc], Colors = new Color32[vc], Indices = new int[ic] };
                foreach (var e in layout.Values) if (e.Offset >= stride) throw new InvalidDataException(source + ": vertex element outside stride.");
                layout.TryGetValue(2, out var normal); bool hasNormal = layout.ContainsKey(2);
                layout.TryGetValue(10, out var uv); bool hasUV = layout.ContainsKey(10);
                layout.TryGetValue(8, out var color); bool hasColor = layout.ContainsKey(8);
                if (hasNormal && normal.Offset + 8 > stride || hasColor && color.Offset + 4 > stride || hasUV && uv.Offset + UVSize(uv.Format) > stride)
                    throw new InvalidDataException(source + ": vertex element overruns stride.");
                for (int v = 0; v < vc; v++)
                {
                    if ((v & 1023) == 0) token.ThrowIfCancellationRequested();
                    int p = checked(vo + v * stride);
                    // Native coordinates are Y-up; reflect Z to Unity and reverse winding below.
                    part.Positions[v] = new Vector3(mg.F32(p), mg.F32(p + 4), -mg.F32(p + 8));
                    if (hasNormal)
                    {
                        int n = p + normal.Offset;
                        part.Normals[v] = new Vector3(Snorm(mg.I16(n)), Snorm(mg.I16(n + 2)), -Snorm(mg.I16(n + 4)));
                    }
                    if (hasUV) part.UV[v] = ReadUV(mg, p + uv.Offset, uv.Format);
                    part.Colors[v] = hasColor ? new Color32(mg.U8(p + color.Offset), mg.U8(p + color.Offset + 1), mg.U8(p + color.Offset + 2), mg.U8(p + color.Offset + 3)) : new Color32(255, 255, 255, 255);
                }
                if (!hasNormal) part.Normals = null;
                for (int t = 0; t < ic; t += 3)
                {
                    int a = mg.U16(indexBase + io + t * 2), b = mg.U16(indexBase + io + (t + 1) * 2), c = mg.U16(indexBase + io + (t + 2) * 2);
                    if (a >= vc || b >= vc || c >= vc) throw new InvalidDataException(source + ": triangle index outside vertex buffer.");
                    part.Indices[t] = a; part.Indices[t + 1] = c; part.Indices[t + 2] = b;
                }
                model.Parts.Add(part);
            }
            if (model.Parts.Count == 0) throw new InvalidDataException(source + ": no drawable head meshes.");
            return model;
        }
        static float Snorm(short v) => Math.Max(-1f, v / 32767f);
        static int UVSize(int format) => format == 2 || format == 3 ? 8 : format == 12 ? 2 : 4;
        static Vector2 ReadUV(Binary b, int o, int format)
        {
            float u, v;
            switch (format)
            {
                case 2: case 3: u = b.F32(o); v = b.F32(o + 4); break;
                case 12: u = b.U8(o) / 255f; v = b.U8(o + 1) / 255f; break;
                case 14: u = b.U16(o) / 65535f; v = b.U16(o + 2) / 65535f; break;
                case 18: case 20: u = b.I16(o) / 32767f; v = b.I16(o + 2) / 32767f; break;
                case 0: case 1: case 10: case 11: case 13: case 16: case 17:
                    u = Half(b.U16(o)); v = Half(b.U16(o + 2)); break;
                default: throw new InvalidDataException("Unsupported UV format " + format);
            }
            if (float.IsNaN(u) || float.IsInfinity(u) || float.IsNaN(v) || float.IsInfinity(v)) throw new InvalidDataException("Non-finite UV.");
            // DDS data is uploaded in native row order; preserve native UVs too.
            return new Vector2(u, v);
        }
        static float Half(ushort value)
        {
            int exp = (value >> 10) & 31, mantissa = value & 1023;
            double v = exp == 0 ? mantissa * Math.Pow(2, -24) : exp == 31 ? double.NaN : (1 + mantissa / 1024.0) * Math.Pow(2, exp - 15);
            return (float)((value & 32768) == 0 ? v : -v);
        }
    }
}
