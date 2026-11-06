using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using G4Composition;
using G4Runtime;
using UnityEngine;

internal sealed class UniformFbxReader
{
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, UniformFbxReader> parsedFiles = new();
    internal static UniformFbxReader Read(UniformFbxModel model) => parsedFiles.GetValue(model.bytesBase64, value => Read(Convert.FromBase64String(value)));
    internal sealed class Bone { internal string Name; internal Matrix4x4 Bind; }
    internal sealed class Part { internal string Name; internal Vector3[] Positions, Normals; internal Vector2[] Uv; internal int[,] Bones; internal float[,] Weights; }
    internal Bone[] Bones;
    internal Part[] Parts;
    [DllImport("UniformFbx", CallingConvention = CallingConvention.Cdecl)]
    static extern int uniform_fbx_read(byte[] data, UIntPtr size, out IntPtr output, out UIntPtr outputSize, StringBuilder error, UIntPtr errorSize);
    [DllImport("UniformFbx", CallingConvention = CallingConvention.Cdecl)] static extern void uniform_fbx_free(IntPtr data);

    internal static UniformFbxReader Read(byte[] bytes)
    {
        if (bytes.Length == 0 || bytes.Length > 64 * 1024 * 1024) throw new InvalidDataException("FBX must be between 1 byte and 64 MB.");
        var error = new StringBuilder(4096); IntPtr output = IntPtr.Zero;
        try {
            if (uniform_fbx_read(bytes, (UIntPtr)bytes.Length, out output, out var size, error, (UIntPtr)4096) == 0) throw new InvalidDataException(error.ToString());
            if (size.ToUInt64() > 256 * 1024 * 1024) throw new InvalidDataException("FBX geometry exceeds memory limit.");
            byte[] packet = new byte[(int)size.ToUInt64()]; Marshal.Copy(output, packet, 0, packet.Length);
            using var stream = new MemoryStream(packet); using var reader = new BinaryReader(stream, Encoding.UTF8);
            if (reader.ReadUInt32() != 0x31424655) throw new InvalidDataException("Unsupported FBX bridge version.");
            int Count(int max) { int count = reader.ReadInt32(); if (count < 0 || count > max) throw new InvalidDataException("Invalid FBX element count."); return count; }
            string Name() { int length = Count(1024 * 1024); var data = reader.ReadBytes(length); if (data.Length != length) throw new EndOfStreamException(); return Encoding.UTF8.GetString(data); }
            Vector3 Vec3() => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
            var result = new UniformFbxReader { Bones = new Bone[Count(4096)] };
            for (int i = 0; i < result.Bones.Length; i++) {
                var bone = new Bone { Name = Name() }; var matrix = new Matrix4x4();
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) matrix[r, c] = reader.ReadSingle(); bone.Bind = matrix; result.Bones[i] = bone;
            }
            result.Parts = new Part[Count(256)];
            for (int i = 0; i < result.Parts.Length; i++) {
                string name = Name(); int count = Count(2 * 1024 * 1024);
                if (count == 0 || count % 3 != 0) throw new InvalidDataException("FBX has empty or invalid mesh: " + name);
                var part = new Part { Name = name, Positions = new Vector3[count], Normals = new Vector3[count], Uv = new Vector2[count], Bones = new int[count, 8], Weights = new float[count, 8] };
                for (int v = 0; v < count; v++) {
                    part.Positions[v] = Vec3(); part.Normals[v] = Vec3().normalized; part.Uv[v] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
                    for (int w = 0; w < 8; w++) part.Bones[v, w] = reader.ReadInt32();
                    float total = 0;
                    for (int w = 0; w < 8; w++) { float value = reader.ReadSingle(); if (value < 0 || float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Invalid FBX skin weight."); part.Weights[v, w] = value; total += value;
                        if (value > 0 && (part.Bones[v, w] < 0 || part.Bones[v, w] >= result.Bones.Length)) throw new InvalidDataException("Invalid FBX skin bone."); }
                    if (total <= 0) throw new InvalidDataException("FBX has an unweighted vertex: " + name);
                    for (int w = 0; w < 8; w++) part.Weights[v, w] /= total;
                }
                result.Parts[i] = part;
            }
            if (stream.Position != stream.Length || result.Parts.Length == 0) throw new InvalidDataException("Invalid FBX geometry packet.");
            return result;
        }
        finally { if (output != IntPtr.Zero) uniform_fbx_free(output); }
    }

    internal static string Canonical(string value) => value.Split('|', ':').Last().ToLowerInvariant().Replace("_wgt_1_0", "");
    internal UniformFbxModel BindAutomatically(byte[] bytes, string fileName, G4Skeleton rig)
    {
        var model = new UniformFbxModel { fileName = fileName, bytesBase64 = Convert.ToBase64String(bytes), meshSlots = new int[Parts.Length] };
        foreach (var bone in Bones) {
            var matches = Enumerable.Range(0, rig.Names.Length).Where(i => rig.Names[i] == bone.Name).ToArray();
            if (matches.Length == 0) matches = Enumerable.Range(0, rig.Names.Length).Where(i => Canonical(rig.Names[i]) == Canonical(bone.Name)).ToArray();
            model.bones.Add(new UniformFbxBoneBinding { sourceName = bone.Name, targetHash = matches.Length == 1 ? rig.Hashes[matches[0]] : 0 });
        }
        return model;
    }

    internal void BindMesh(UniformFbxModel settings, G4Skeleton rig, int slot, out Mesh mesh, out uint[] hashes, out ushort[] weights)
    {
        settings.Validate();
        if (settings.bones.Count != Bones.Length || settings.meshSlots.Length != Parts.Length) throw new InvalidDataException("FBX binding does not match the source file.");
        var mirror = Matrix4x4.Scale(new Vector3(1, 1, -1));
        var transforms = new Matrix4x4[Bones.Length];
        for (int i = 0; i < Bones.Length; i++) {
            if (settings.bones[i].sourceName != Bones[i].Name || !rig.ByHash.TryGetValue(settings.bones[i].targetHash, out int target)) throw new InvalidDataException("Missing target bone: " + Bones[i].Name);
            // Vertices are already in normalized scene/world space. FBX bone
            // axes (including Blender's bone rolls) need not match G4 axes.
            // Copy weights to the target skeleton without rotating that baked
            // rest geometry a second time; only align the bone anchor positions.
            Vector3 sourceAnchor = Bones[i].Bind.GetColumn(3);
            Vector3 targetAnchor = (mirror * rig.Bind[target] * mirror).GetColumn(3);
            transforms[i] = Matrix4x4.TRS(targetAnchor, Quaternion.identity, Vector3.one * settings.scale) * Matrix4x4.Translate(-sourceAnchor);
        }
        var positions = new List<Vector3>(); var normals = new List<Vector3>(); var uv = new List<Vector2>(); var hashList = new List<uint>(); var weightList = new List<ushort>();
        for (int p = 0; p < Parts.Length; p++) {
            if (settings.meshSlots[p] != slot) continue; var part = Parts[p];
            for (int v = 0; v < part.Positions.Length; v++) {
                Vector3 point = Vector3.zero, normal = Vector3.zero; int sum = 0, first = weightList.Count;
                for (int w = 0; w < 8; w++) {
                    float amount = part.Weights[v, w]; int source = part.Bones[v, w];
                    if (amount > 0) { point += transforms[source].MultiplyPoint3x4(part.Positions[v]) * amount; normal += transforms[source].inverse.transpose.MultiplyVector(part.Normals[v]) * amount; }
                    hashList.Add(amount > 0 ? settings.bones[source].targetHash : 0); ushort quantized = (ushort)Mathf.RoundToInt(amount * 65535); weightList.Add(quantized); sum += quantized;
                }
                weightList[first] = checked((ushort)(weightList[first] + 65535 - sum));
                if (new[] { point.x, point.y, point.z, normal.x, normal.y, normal.z, part.Uv[v].x, part.Uv[v].y }.Any(value => float.IsNaN(value) || float.IsInfinity(value))) throw new InvalidDataException("FBX has non-finite geometry.");
                positions.Add(point); normals.Add(normal.normalized); uv.Add(new Vector2(part.Uv[v].x, settings.flipV ? 1 - part.Uv[v].y : part.Uv[v].y));
            }
        }
        if (positions.Count > ushort.MaxValue) throw new InvalidDataException("A target mesh exceeds 65,535 vertices. Split it between more target slots.");
        // ufbx preserves FBX face winding while converting to left-handed axes.
        // Unity's front faces use the opposite winding. Reverse each triangle
        // once here; the G4 encoder handles its own handedness conversion later.
        var indices = Enumerable.Range(0, positions.Count).ToArray();
        for (int i = 0; i < indices.Length; i += 3) { indices[i + 1] = i + 2; indices[i + 2] = i + 1; }
        mesh = new Mesh { name = "FBX_" + slot }; mesh.SetVertices(positions); mesh.SetNormals(normals); mesh.SetUVs(0, uv); mesh.triangles = indices; mesh.RecalculateBounds(); hashes = hashList.ToArray(); weights = weightList.ToArray();
    }
}
