using System;
using System.Collections.Generic;
using System.IO;
using G4Runtime;
using UnityEngine;

namespace G4Composition
{
    // Sample the game's static additive chest clips, retaining all eight skin
    // influences when baking them into the uniform and skin previews.
    public static class NativeBodyShape
    {
        static readonly Dictionary<string, G4Skeleton> rigs = new Dictionary<string, G4Skeleton>();
        static readonly Dictionary<string, Dictionary<uint, AvatarBoneTransform>> poses = new Dictionary<string, Dictionary<uint, AvatarBoneTransform>>();

        // UI/save values are -1 (disabled) or 0/1/2; CHARA_MODEL_INFO[31] uses 1/2/3.
        // Native zero disables the additive chest pose rather than selecting small.
        public static int ToNativeChestSize(int size)
        {
            if (size < -1 || size > 2) throw new ArgumentOutOfRangeException(nameof(size));
            return size + 1;
        }

        public static int FromNativeChestSize(int value)
        {
            if (value < 0 || value > 3) throw new ArgumentOutOfRangeException(nameof(value));
            return value - 1;
        }

        public static G4Skeleton Skeleton(CfgTable.Row body)
        {
            string name = Path.GetFileNameWithoutExtension(body.Text(1).Replace((char)92, '/'));
            string path = Path.Combine(DumpDataManager.CommonPath, "chr", name, name + ".g4sk");
            if (!rigs.TryGetValue(path, out var rig)) rigs.Add(path, rig = G4Skeleton.Read(File.ReadAllBytes(path), path, "Native body preview"));
            return rig;
        }

        public static IReadOnlyDictionary<uint, AvatarBoneTransform> Chest(CfgTable.Row body, int size)
        {
            if (size < -1 || size > 2) throw new ArgumentOutOfRangeException(nameof(size));
            if (size == -1) return new Dictionary<uint, AvatarBoneTransform>();
            var rig = Skeleton(body);
            string path = Path.ChangeExtension(rig.Path, null) + "_p010.g4pk";
            string key = path + "|" + size;
            if (poses.TryGetValue(key, out var pose)) return pose;
            pose = new Dictionary<uint, AvatarBoneTransform>();
            // Small/medium/large are explicit clips where present. Some body
            // families already have the requested small or medium shape in bind
            // pose and consequently omit that additive clip.
            uint hash = size == 0 ? 0x5ab4c3f8u : size == 1 ? 0x4ba9b305u : 0x1cc666feu;
            if (!ReadPose(ReadMotion(path), hash, pose) && size == 2)
                throw new InvalidDataException("Missing native large-chest pose: " + path);
            poses.Add(key, pose);
            return pose;
        }

        public static IReadOnlyDictionary<uint, AvatarBoneTransform> Outfit(CfgTable.Row body, int size, int hem)
        {
            if (hem != 1) return Chest(body, size);
            string path = Path.ChangeExtension(Skeleton(body).Path, null) + "_p010.g4pk";
            string key = path + "|out|" + size;
            if (poses.TryGetValue(key, out var cached)) return cached;
            var result = new Dictionary<uint, AvatarBoneTransform>(Chest(body, size));
            var shirt = new Dictionary<uint, AvatarBoneTransform>();
            if (!ReadPose(ReadMotion(path), 0x2cb5c17du, shirt)) throw new InvalidDataException("Missing native shirt-out pose: " + path);
            foreach (var pair in shirt)
            {
                if (result.TryGetValue(pair.Key, out var chest))
                    result[pair.Key] = new AvatarBoneTransform {
                        Position = chest.Position + pair.Value.Position,
                        Scale = Vector3.Scale(chest.Scale, pair.Value.Scale),
                        Rotation = (Quaternion.Euler(chest.Rotation * Mathf.Rad2Deg) *
                            Quaternion.Euler(pair.Value.Rotation * Mathf.Rad2Deg)).eulerAngles * Mathf.Deg2Rad };
                else result.Add(pair.Key, pair.Value);
            }
            poses.Add(key, result);
            return result;
        }

        static byte[] ReadMotion(string path)
        {
            byte[] pack = File.ReadAllBytes(path); var b = new G4ModelParser.Binary(pack, path); b.Magic("G4PK");
            int table = b.U16(4), count = b.Int(0x20);
            for (int i = 0; i < count; i++)
            {
                int offset = checked(table + b.Int(table + i * 4) * 4), size = b.Int(table + (count + i) * 4);
                b.Range(offset, size);
                if (size < 4 || pack[offset] != 'G' || pack[offset+1] != '4' || pack[offset+2] != 'M' || pack[offset+3] != 'T') continue;
                var data = new byte[size]; Buffer.BlockCopy(pack, offset, data, 0, size); return data;
            }
            throw new InvalidDataException("Body motion package has no G4MT: " + path);
        }

        static bool ReadPose(byte[] data, uint clipHash, Dictionary<uint, AvatarBoneTransform> result)
        {
            var b = new G4ModelParser.Binary(data, "Native chest pose"); b.Magic("G4MT");
            int header = b.U16(4), shift = b.U8(0x36);
            int Section(int f, int bits = 0) => checked(header + (b.U16(f) << bits) * 4);
            int hashes = Section(0x2a), targets = Section(0x2c), scales = Section(0x28);
            int infos = Section(0x24, shift), channels = Section(0x26, shift), values = Section(0x32, shift * 2);
            int clip = -1;
            for (int i = 0; i < b.U16(0x20); i++) if (b.U32(hashes + i * 4) == clipHash) { clip = header + i * 16; break; }
            if (clip < 0) return false;
            if ((b.U8(clip+8) & 1) == 0) throw new InvalidDataException("Chest clip is not additive.");
            int first = b.U16(clip + 4) + (b.U8(clip + 10) << 16), count = b.U16(clip + 6);
            for (int i = 0; i < count; i++)
            {
                int info = infos + (first + i) * 8;
                uint bone = b.U32(targets + b.U16(info) * 4);
                var change = new AvatarBoneTransform(); result.Add(bone, change);
                int channel = b.U16(info + 2) + (b.U8(info + 5) << 16);
                for (int j = 0; j < b.U8(info + 4); j++)
                {
                    int c = channels + (channel + j) * 20;
                    if (b.Int(c + 16) == 0) continue;
                    int type = b.U8(c), components = b.U8(c+4), at = values + b.Int(c + 12);
                    float scale = b.F32(scales + b.U8(c+6) * 4);
                    var v = new float[components];
                    for (int k = 0; k < components; k++) v[k] = Decode(b, c, at, k, scale);
                    if (type >= 1 && type <= 3) change.Scale[type-1] = 1f + v[0];
                    else if (type >= 10 && type <= 12) change.Position[type-10] = v[0];
                    else if (type == 9 && components == 4)
                        change.Rotation = new Quaternion(v[0],v[1],v[2],v[3]).normalized.eulerAngles * Mathf.Deg2Rad;
                    else throw new InvalidDataException("Unsupported chest channel " + type);
                }
            }
            return true;
        }
        static float Decode(G4ModelParser.Binary b, int c, int at, int component, float scale)
        {
            int codec = b.U8(c+1), width = b.U8(c+3);
            if (codec == 1 && width == 4) return b.F32(at+component*4);
            if (width != 1 && width != 2) throw new InvalidDataException("Unsupported chest value width.");
            int offset = at + component * width;
            if (codec == 1) return width == 1 ? (sbyte)b.U8(offset) : unchecked((short)b.U16(offset));
            if (codec == 2) return (width == 1 ? b.U8(offset) / 256f : b.U16(offset) / 65536f) * scale;
            if (codec == 3) return (width == 1 ? (sbyte)b.U8(offset) / 128f : unchecked((short)b.U16(offset)) / 32768f) * scale;
            throw new InvalidDataException("Unsupported native chest codec " + codec);
        }
    }
}
