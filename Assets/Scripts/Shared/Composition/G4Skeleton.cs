using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;
using G4Runtime;

namespace G4Composition
{
    public sealed class G4Skeleton
    {
        public string Path, Evidence, SourceDigest;
        public byte[] Bytes;
        public string[] Names;
        public uint[] Hashes;
        public int[] Parents;
        public Matrix4x4[] Bind, InverseBind;
        public readonly Dictionary<uint, int> ByHash = new Dictionary<uint, int>();
        public static G4Skeleton Read(byte[] data, string path, string evidence)
        {
            var b = new G4ModelParser.Binary(data, path); b.Magic("G4SK");
            int n = b.U16(0x20), inverse = 0x40 + b.U16(0x24) * 4, parents = 0x40 + b.U16(0x2a) * 4, names = 0x40 + b.U16(0x32) * 4;
            if (n == 0) throw new InvalidDataException(path + ": empty skeleton.");
            var rig = new G4Skeleton { Path = path, Evidence = evidence, Bytes = data, Names = new string[n], Hashes = new uint[n], Parents = new int[n], Bind = new Matrix4x4[n], InverseBind = new Matrix4x4[n] };
            for (int i = 0; i < n; i++)
            {
                rig.Names[i] = b.Text(names + b.U16(names + i * 2)); rig.Hashes[i] = G4TextureParser.Crc32(rig.Names[i]);
                if (rig.ByHash.ContainsKey(rig.Hashes[i])) throw new InvalidDataException(path + ": duplicate bone hash.");
                rig.ByHash.Add(rig.Hashes[i], i); rig.Parents[i] = b.U16(parents + i * 2);
                rig.Bind[i] = Matrix(b, 0x40 + i * 48); rig.InverseBind[i] = Matrix(b, inverse + i * 48);
                Matrix4x4 identity = rig.Bind[i] * rig.InverseBind[i];
                for (int r = 0; r < 4; r++) for (int c = 0; c < 4; c++) if (Math.Abs(identity[r, c] - (r == c ? 1 : 0)) > 0.005f) throw new InvalidDataException(path + ": invalid inverse bind for " + rig.Names[i]);
            }
            for (int i = 0; i < n; i++)
            {
                var visited = new HashSet<int>(); int p = i;
                while (p < n) { if (!visited.Add(p)) throw new InvalidDataException(path + ": cyclic bone hierarchy."); int next = rig.Parents[p]; if (next == p) break; p = next; }
            }
            return rig;
        }
        static Matrix4x4 Matrix(G4ModelParser.Binary b, int offset)
        {
            var m = Matrix4x4.identity;
            for (int r = 0; r < 3; r++) for (int c = 0; c < 4; c++) m[r, c] = b.F32(offset + (r * 4 + c) * 4);
            return m;
        }
    }
    public sealed class SkeletonResolver
    {
        readonly string common;
        readonly Dictionary<string, G4Skeleton> cache = new Dictionary<string, G4Skeleton>(StringComparer.OrdinalIgnoreCase);
        CfgTable models;
        public SkeletonResolver(string commonPath) { common = commonPath; }
        public G4Skeleton Resolve(RawHeadAsset asset, CancellationToken token)
        {
            var used = new HashSet<uint>(asset.Meshes.SelectMany(m => m.Palette));
            var candidates = new List<Tuple<string, string>>();
            Action<string, string> add = (path, why) => { candidates.Add(Tuple.Create(System.IO.Path.ChangeExtension(path, ".g4sk"), why)); candidates.Add(Tuple.Create(System.IO.Path.ChangeExtension(path, ".g4pkm"), why + " (G4PKM)")); };
            add(asset.ModelPath, "同名骨架");
            string directory = System.IO.Path.GetDirectoryName(asset.ModelPath);
            add(System.IO.Path.Combine(directory, new DirectoryInfo(directory).Name), "同資料夾主要模型");
            string stem = System.IO.Path.GetFileNameWithoutExtension(asset.ModelPath);
            if (stem.EndsWith("1", StringComparison.Ordinal)) add(System.IO.Path.Combine(directory, stem.Substring(0, stem.Length - 1) + "0"), "角色變體");
            foreach (var candidate in candidates) { var rig = Try(candidate.Item1, candidate.Item2, used); if (rig != null) return rig; }
            string tableDirectory = System.IO.Path.Combine(common, "gamedata", "character");
            if (models == null)
            {
                string[] paths = Directory.GetFiles(tableDirectory, "chara_model_*.cfg.bin").Where(p => System.Text.RegularExpressions.Regex.IsMatch(System.IO.Path.GetFileName(p), @"^chara_model_\d")).ToArray();
                if (paths.Length != 1) throw new InvalidDataException("無法唯一判定 chara_model CFGBIN 版本。");
                models = CfgTable.Read(File.ReadAllBytes(paths[0]), paths[0]);
            }
            string relative = Normalize(asset.ModelPath.Substring(common.Length + 1));
            var modelRows = models.Rows.Where(r => r.Name == "CHARA_MODEL_INFO" && r.Values.Length > 10 && (Normalize(r.Text(1)) == relative || Normalize(r.Text(10)) == relative)).ToArray();
            var configured = new List<G4Skeleton>();
            foreach (var row in modelRows)
                foreach (var body in models.Rows.Where(r => r.Name == "CHARA_BODY_INFO" && r.Values.Length > 1 && r.UInt(0) == row.UInt(4)))
                {
                    string bodyPath = System.IO.Path.Combine(common, Normalize(body.Text(1)).Replace('/', System.IO.Path.DirectorySeparatorChar));
                    var rig = Try(System.IO.Path.ChangeExtension(bodyPath, ".g4sk"), "目前 Dump CFGBIN: CHARA_MODEL_INFO → CHARA_BODY_INFO " + row.UInt(4), used);
                    if (rig != null && configured.All(r => !r.Bytes.SequenceEqual(rig.Bytes))) configured.Add(rig);
                }
            if (configured.Count == 1) return configured[0];
            if (configured.Count > 1) throw new InvalidDataException(asset.RelativePath + ": CFGBIN 指向多個不同骨架。");
            var hair = Try(System.IO.Path.Combine(common, "chr", "_face", "20_EDIT", "_hairSK", "_hairSK.g4sk"), "共用髮骨", used);
            if (hair != null) return hair;
            var full = new List<G4Skeleton>();
            foreach (string path in Directory.EnumerateFiles(System.IO.Path.Combine(common, "chr"), "*.g4sk", SearchOption.AllDirectories))
            {
                token.ThrowIfCancellationRequested(); var rig = Try(path, "完整骨骼 hash 覆蓋", used);
                if (rig != null && full.All(r => !r.Bytes.SequenceEqual(rig.Bytes))) full.Add(rig);
                if (full.Count > 1) break;
            }
            if (full.Count == 1) return full[0];
            throw new InvalidDataException(asset.RelativePath + (full.Count > 1 ? ": 骨架匹配不唯一，需要明確角色骨架。" : ": 找不到涵蓋全部引用的骨架。"));
        }
        static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            path = path.Replace('\\', '/').TrimStart('/');
            if (path.StartsWith("_common/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(8);
            if (path.StartsWith("common/", StringComparison.OrdinalIgnoreCase)) path = path.Substring(7);
            if (!path.StartsWith("chr/", StringComparison.OrdinalIgnoreCase)) path = "chr/" + path;
            return System.IO.Path.ChangeExtension(path, ".g4md").Replace('\\', '/').ToLowerInvariant();
        }
        G4Skeleton Try(string path, string why, HashSet<uint> used)
        {
            if (!File.Exists(path)) return null;
            if (!cache.TryGetValue(path, out var rig))
            {
                byte[] data = File.ReadAllBytes(path);
                string sourceDigest = RawHeadAsset.Digest(data);
                if (path.EndsWith(".g4pkm", StringComparison.OrdinalIgnoreCase))
                {
                    var b = new G4ModelParser.Binary(data, path); b.Magic("G4PK"); int n = b.Int(0x20), table = b.U16(4); b.Range(table, n * 8L);
                    byte[] skeleton = null;
                    for (int i = 0; i < n; i++)
                    {
                        int off = checked(table + b.Int(table + i * 4) * 4), size = b.Int(table + n * 4 + i * 4); b.Range(off, size);
                        if (size >= 4 && System.Text.Encoding.ASCII.GetString(data, off, 4) == "G4SK") { if (skeleton != null) return null; skeleton = data.Skip(off).Take(size).ToArray(); }
                    }
                    if (skeleton == null) return null; data = skeleton;
                }
                rig = G4Skeleton.Read(data, path, why); rig.SourceDigest = sourceDigest; cache.Add(path, rig);
            }
            return used.All(rig.ByHash.ContainsKey) ? rig : null;
        }
    }
}


