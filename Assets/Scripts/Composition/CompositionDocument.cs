using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace G4Composition
{
    [Serializable] public sealed class BoneMapping
    {
        public uint SourceHash, TargetHash;
        public bool Manual;
    }
    [Serializable] public sealed class CompositionPart
    {
        public string Id = Guid.NewGuid().ToString("N"), Name, SourceRelativePath;
        public int MeshIndex;
        public int[] Triangles;
        public Vector3 Position, Rotation, Scale = Vector3.one;
        public bool Visible = true;
        public List<BoneMapping> Bones = new List<BoneMapping>();
        public Matrix4x4 Matrix
        {
            get
            {
                foreach (float value in new[] { Position.x, Position.y, Position.z, Rotation.x, Rotation.y, Rotation.z, Scale.x, Scale.y, Scale.z })
                    if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException(Name + ": non-finite transform.");
                if (Scale.x <= 0 || Scale.y <= 0 || Scale.z <= 0) throw new InvalidDataException(Name + ": scale must be positive.");
                return Matrix4x4.TRS(Position, Quaternion.Euler(Rotation), Scale);
            }
        }
    }
    [Serializable] public sealed class CompositionDocument
    {
        public int Version = 1;
        public string Id = Guid.NewGuid().ToString("N");
        public List<CompositionPart> Parts = new List<CompositionPart>();
    }
    public enum ExportMode { ReplaceCharacter, NewCharacter }
    [Serializable] public sealed class ExportRequest
    {
        public ExportMode Mode;
        public CompositionDocument Document;
        public string TargetRelativePath, DestinationFolder;
        public NewCharacterSettings Character = new NewCharacterSettings();
    }
    [Serializable] public sealed class NewCharacterSettings
    {
        public uint TemplateBaseId, TemplateParameterId;
        public string DisplayName = "New character";
        public int Affinity, MainPosition, SubPosition;
        public int[] StatModifiers = new int[7];
        public uint[] Skills = new uint[9];
        public int[] UnlockLevels = new int[9];
    }
    public static class ConnectedComponents
    {
        // Adjacency is index based. Coincident positions intentionally stay separate.
        public static IReadOnlyList<int[]> Split(int vertexCount, int[] indices)
        {
            if (vertexCount < 0 || indices == null || indices.Length % 3 != 0) throw new InvalidDataException("Invalid triangle buffer.");
            int[] parent = Enumerable.Range(0, vertexCount).ToArray();
            Func<int, int> find = null;
            find = v => { while (parent[v] != v) { parent[v] = parent[parent[v]]; v = parent[v]; } return v; };
            for (int i = 0; i < indices.Length; i += 3)
            {
                for (int j = 0; j < 3; j++) if (indices[i + j] < 0 || indices[i + j] >= vertexCount) throw new InvalidDataException("Triangle index out of range.");
                int a = find(indices[i]); parent[find(indices[i + 1])] = a; parent[find(indices[i + 2])] = a;
            }
            var groups = new Dictionary<int, List<int>>();
            for (int i = 0; i < indices.Length; i += 3)
            {
                int root = find(indices[i]);
                if (!groups.TryGetValue(root, out var triangles)) groups.Add(root, triangles = new List<int>());
                triangles.Add(i / 3);
            }
            return groups.Values.OrderBy(g => g[0]).Select(g => g.ToArray()).ToArray();
        }
        public static Mesh Extract(Mesh source, IEnumerable<int> triangles)
        {
            var original = source.triangles; var selected = triangles.Distinct().OrderBy(t => t).ToArray();
            var vertices = new List<int>(); var remap = new Dictionary<int, int>(); var indices = new List<int>();
            foreach (int triangle in selected)
            {
                if (triangle < 0 || triangle >= original.Length / 3) throw new InvalidDataException("Invalid selected triangle.");
                for (int j = 0; j < 3; j++)
                {
                    int v = original[triangle * 3 + j];
                    if (!remap.TryGetValue(v, out int id)) { id = vertices.Count; vertices.Add(v); remap.Add(v, id); }
                    indices.Add(id);
                }
            }
            var mesh = new Mesh { name = source.name, indexFormat = source.indexFormat };
            var pos = source.vertices; var normals = source.normals; var uv = source.uv; var colors = source.colors32;
            mesh.vertices = vertices.Select(v => pos[v]).ToArray();
            if (normals.Length == pos.Length) mesh.normals = vertices.Select(v => normals[v]).ToArray();
            if (uv.Length == pos.Length) mesh.uv = vertices.Select(v => uv[v]).ToArray();
            if (colors.Length == pos.Length) mesh.colors32 = vertices.Select(v => colors[v]).ToArray();
            mesh.triangles = indices.ToArray(); mesh.RecalculateBounds(); return mesh;
        }
    }
}
