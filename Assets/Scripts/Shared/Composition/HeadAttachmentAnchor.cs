using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using G4Runtime;
using UnityEngine;

namespace G4Composition
{
    // Geometry is in the model root's space. Preview centering must not move
    // its anatomical attachment point or depend on facial mesh bounds.
    public sealed class HeadAttachmentAnchor : MonoBehaviour
    {
        [HideInInspector] public string SourceModelPath;
        [HideInInspector] public bool HasFixedPosition;
        [HideInInspector] public Vector3 FixedPosition;
        static string cachedCommon;
        static SkeletonResolver resolver;
        static readonly Dictionary<string, Vector3> positions = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);
        bool warned;

        public bool TryGetPosition(out Vector3 position)
        {
            if (HasFixedPosition) { position = FixedPosition; return true; }
            try
            {
                position = GetModelPosition(SourceModelPath);
                return true;
            }
            catch (Exception e)
            {
                position = default;
                if (!warned) Debug.LogWarning("Cannot resolve head attachment; using mesh bounds: " + e.Message, this);
                warned = true;
                return false;
            }
        }

        public static Vector3 GetBody(CfgTable.Row body)
        {
            string path = body.Text(1)?.Replace((char)92, '/').TrimStart('/');
            if (string.IsNullOrEmpty(path)) throw new InvalidDataException("Body has no skeleton path.");
            if (path.StartsWith("_common/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(8);
            if (path.StartsWith("common/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(7);
            if (!path.StartsWith("chr/", StringComparison.OrdinalIgnoreCase)) path = "chr/" + path;
            return GetModelPosition(Path.Combine(DumpDataManager.CommonPath, path));
        }

        static Vector3 GetModelPosition(string path)
        {
            EnsureResolver();
            if (!positions.TryGetValue(path, out Vector3 position))
            {
                var asset = new RawHeadAsset { ModelPath = path, RelativePath = path };
                asset.Meshes.Add(new RawMesh { Palette = new[] { G4TextureParser.Crc32("c_head_1_0") } });
                position = Get(asset);
                positions.Add(path, position);
            }
            return position;
        }

        public static Vector3 Get(RawHeadAsset asset)
        {
            if (asset.RelativePath.StartsWith("Avatar/", StringComparison.OrdinalIgnoreCase)) return Vector3.zero;
            EnsureResolver();
            var rig = asset.Skeleton ?? (asset.Skeleton = resolver.Resolve(asset, CancellationToken.None));
            // The shared edit rig has a head-local origin, not a native body's
            // world-space head position. Do not use it as a native fallback.
            if (rig.Evidence == "共用髮骨") throw new InvalidDataException(asset.RelativePath + ": no native body skeleton.");
            if (!rig.ByHash.TryGetValue(G4TextureParser.Crc32("c_head_1_0"), out int head))
                throw new InvalidDataException(asset.RelativePath + ": no head attachment bone.");
            Vector3 point = rig.Bind[head].MultiplyPoint3x4(Vector3.zero);
            return new Vector3(point.x, point.y, -point.z);
        }

        static void EnsureResolver()
        {
            if (resolver != null && cachedCommon == DumpDataManager.CommonPath) return;
            cachedCommon = DumpDataManager.CommonPath;
            resolver = new SkeletonResolver(cachedCommon);
            positions.Clear();
        }
    }
}
