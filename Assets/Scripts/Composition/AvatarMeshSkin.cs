using System.Collections.Generic;
using System.IO;
using G4Runtime;
using UnityEngine;

namespace G4Composition
{
    // Keep eight authored influences while the static preview bakes geometry/UVs.
    public sealed class AvatarMeshSkin : MonoBehaviour
    {
        [HideInInspector] public Rect LegacyAtlasBounds = new Rect(0, 0, 1, 1);
        [HideInInspector] public uint[] Hashes;
        [HideInInspector] public ushort[] Weights;

        public void Capture(G4ModelParser.Part part, G4Skeleton skeleton)
        {
            Hashes = new uint[part.Positions.Length * 8];
            Weights = new ushort[Hashes.Length];
            uint head = G4TextureParser.Crc32("c_head_1_0");
            for (int v = 0; v < part.Positions.Length; v++)
            {
                if (part.BonePalette == null)
                {
                    Hashes[v * 8] = head; Weights[v * 8] = ushort.MaxValue;
                    continue;
                }
                for (int j = 0; j < 8; j++)
                {
                    int at = v * 8 + j;
                    Weights[at] = part.BoneWeights[v, j];
                    if (Weights[at] == 0) continue;
                    uint hash = part.BonePalette[part.BoneIndices[v, j]];
                    if (!skeleton.ByHash.TryGetValue(hash, out int bone))
                        throw new InvalidDataException("Missing Avatar skin joint: " + hash.ToString("X8"));
                    string name = skeleton.Names[bone];
                    // Edit cheek/jaw/orbit bones already shaped this mesh.
                    // Preserve hair/attachment bones; the rest follows the head.
                    Hashes[at] = name.Contains("_hir") || name.Contains("_prt") || name.Contains("_atc") ? hash : head;
                }
            }
        }

        public void Combine(IReadOnlyList<AvatarMeshSkin> sources, IReadOnlyList<CombineInstance> instances, int vertexCount)
        {
            var hashes = new List<uint>(); var weights = new List<ushort>();
            uint head = G4TextureParser.Crc32("c_head_1_0");
            for (int i = 0; i < instances.Count; i++)
            {
                int count = instances[i].mesh.vertexCount * 8;
                var skin = sources[i];
                if (skin != null)
                {
                    if (skin.Hashes == null || skin.Weights == null || skin.Hashes.Length != count || skin.Weights.Length != count)
                        throw new InvalidDataException("Avatar skin/vertex count mismatch.");
                    hashes.AddRange(skin.Hashes); weights.AddRange(skin.Weights);
                }
                else for (int v = 0; v < count; v++)
                {
                    hashes.Add(v % 8 == 0 ? head : 0); weights.Add(v % 8 == 0 ? ushort.MaxValue : (ushort)0);
                }
            }
            if (hashes.Count != vertexCount * 8) throw new InvalidDataException("Combined Avatar skin/vertex count mismatch.");
            Hashes = hashes.ToArray(); Weights = weights.ToArray();
        }
    }
}