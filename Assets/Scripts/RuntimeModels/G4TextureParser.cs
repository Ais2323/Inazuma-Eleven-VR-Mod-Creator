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
    public static class G4TextureParser
    {
        public sealed class TextureData
        {
            public string Name;
            public uint Hash;
            public int Width, Height, MipCount;
            public TextureFormat Format;
            public byte[] Pixels;
        }
        public static List<TextureData> Parse(byte[] data, string source, List<string> warnings, CancellationToken token = default, bool baseOnly = true)
        {
            var b = new G4ModelParser.Binary(data, source); b.Magic("G4TX"); b.Range(0, 0x26);
            int header = b.U16(4), tableSize = b.Int(12), count = b.U16(0x20), total = b.U16(0x22);
            b.Range(header, count * 48L);
            if (total < count) throw new InvalidDataException(source + ": invalid texture count.");
            int pos = checked(header + count * 48 + b.U8(0x25) * 24);
            pos = checked(((pos + 15) & ~15) + total * 4);
            pos = checked((pos + total + 3) & ~3);
            int names = pos, payloadBase = checked((header + tableSize + 15) & ~15);
            b.Range(names, total * 2L);
            var result = new List<TextureData>();
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                string name = b.Text(checked(names + b.U16(names + i * 2)));
                if (baseOnly && !IsBase(name)) continue;
                try
                {
                    int entry = header + i * 48, offset = checked(payloadBase + b.Int(entry + 4)), size = b.Int(entry + 8);
                    b.Range(offset, size);
                    var payload = new byte[size]; Buffer.BlockCopy(data, offset, payload, 0, size);
                    var texture = ParseDDS(payload, source + "/" + name);
                    texture.Name = name; texture.Hash = Crc32(name); result.Add(texture);
                }
                catch (Exception e) when (e is InvalidDataException || e is OverflowException)
                {
                    warnings.Add(source + "/" + name + ": " + e.Message);
                }
            }
            return result;
        }
        public static TextureData ParseDDS(byte[] payload, string source)
        {
            var b = new G4ModelParser.Binary(payload, source); b.Magic("DDS "); b.Range(0, 128);
            if (b.Int(4) != 124 || b.Int(76) != 32) throw new InvalidDataException(source + ": invalid DDS header.");
            int width = b.Int(16), height = b.Int(12), mips = Math.Max(1, b.Int(28));
            if (width < 1 || height < 1 || width > 16384 || height > 16384 || mips > 1 + (int)Math.Floor(Math.Log(Math.Max(width, height), 2)))
                throw new InvalidDataException(source + ": invalid DDS dimensions/mip count.");
            if (b.Int(24) > 1 || (b.U32(112) & 0xFE00) != 0) throw new InvalidDataException(source + ": only 2D DDS textures are supported.");
            string fourCC = Encoding.ASCII.GetString(payload, 84, 4);
            int start = 128, blockSize = 0; bool rgba = false, bgra = false;
            TextureFormat format;
            if (fourCC == "DXT1") { format = TextureFormat.DXT1; blockSize = 8; }
            else if (fourCC == "DXT5") { format = TextureFormat.DXT5; blockSize = 16; }
            else if (fourCC == "DX10")
            {
                b.Range(128, 20); start = 148;
                if (b.Int(132) != 3 || b.Int(140) != 1 || (b.U32(136) & 4) != 0) throw new InvalidDataException(source + ": unsupported DDS resource dimension/array.");
                switch (b.Int(128))
                {
                    case 71: case 72: format = TextureFormat.DXT1; blockSize = 8; break;
                    case 77: case 78: format = TextureFormat.DXT5; blockSize = 16; break;
                    case 98: case 99: format = TextureFormat.BC7; blockSize = 16; break;
                    case 28: case 29: format = TextureFormat.RGBA32; rgba = true; break;
                    case 87: case 91: format = TextureFormat.RGBA32; rgba = bgra = true; break;
                    default: throw new InvalidDataException(source + ": unsupported DXGI format " + b.Int(128));
                }
            }
            else if ((b.U32(80) & 0x40) != 0 && b.Int(88) == 32)
            {
                format = TextureFormat.RGBA32; rgba = true;
                uint r = b.U32(92), g = b.U32(96), blue = b.U32(100), alpha = b.U32(104);
                bgra = r == 0xFF0000 && g == 0xFF00 && blue == 0xFF;
                if ((!bgra && !(r == 0xFF && g == 0xFF00 && blue == 0xFF0000)) || (alpha != 0 && alpha != 0xFF000000))
                    throw new InvalidDataException(source + ": unsupported RGBA channel masks.");
            }
            else throw new InvalidDataException(source + ": unsupported DDS FourCC " + fourCC);
            int length = 0, w = width, h = height;
            for (int i = 0; i < mips; i++)
            {
                length = checked(length + (rgba ? checked(w * h * 4) : checked(Math.Max(1, (w + 3) / 4) * Math.Max(1, (h + 3) / 4) * blockSize)));
                w = Math.Max(1, w / 2); h = Math.Max(1, h / 2);
            }
            b.Range(start, length);
            var pixels = new byte[length]; Buffer.BlockCopy(payload, start, pixels, 0, length);
            if (rgba)
            {
                if ((b.U32(8) & 8) != 0 && b.Int(20) != width * 4) throw new InvalidDataException(source + ": padded RGBA rows are unsupported.");
                bool opaque = fourCC != "DX10" && b.U32(104) == 0;
                for (int i = 0; i < length; i += 4)
                {
                    if (bgra) { byte temp = pixels[i]; pixels[i] = pixels[i + 2]; pixels[i + 2] = temp; }
                    if (opaque) pixels[i + 3] = 255;
                }
            }
            return new TextureData { Width = width, Height = height, MipCount = mips, Format = format, Pixels = pixels };
        }
        public static bool IsBase(string name)
        {
            string n = name.ToLowerInvariant();
            return !Regex.IsMatch(n, @"(?:\.2|\.a|_n|_nm|nml|_normal|msk|_mask|spm|sp|oc|line|ramp|dp)$") && !n.Contains("cubemap");
        }
        static string Key(string name) => Regex.Replace(name.ToLowerInvariant().TrimEnd('M', 'm').Trim('_'), @"\.(?:\d+|[a-z])$", "");
        public static TextureData Choose(G4ModelParser.Surface material, List<TextureData> textures)
        {
            foreach (uint hash in material.TextureHashes)
            {
                var exact = textures.FirstOrDefault(t => t.Hash == hash);
                if (exact != null && IsBase(exact.Name)) return exact;
            }
            string key = Key(material.Name);
            var keys = new[] { key, Regex.Replace(key, @"_(?:bf|of)$", ""), Regex.Replace(key, @"_\d+$", "") };
            foreach (string candidate in keys)
            {
                var matches = textures.Where(t => IsBase(t.Name) && Key(t.Name) == candidate).OrderBy(t => t.Name, StringComparer.Ordinal).ToArray();
                if (matches.Length > 0) return matches[0];
            }
            var prefix = textures.Where(t => IsBase(t.Name) && (Key(t.Name).StartsWith(key + "_", StringComparison.Ordinal) || key.StartsWith(Key(t.Name) + "_", StringComparison.Ordinal))).ToArray();
            return prefix.Length == 1 ? prefix[0] : null;
        }
        public static TextureData ChooseSuffix(G4ModelParser.Surface material, List<TextureData> textures, string suffix)
        {
            string pattern = Regex.Escape(suffix.ToLowerInvariant()) + @"(?:\.\d+|\.a)?$";
            foreach (uint hash in material.TextureHashes)
            {
                var exact = textures.FirstOrDefault(t => t.Hash == hash);
                if (exact != null && Regex.IsMatch(exact.Name.ToLowerInvariant(), pattern)) return exact;
            }
            return null;
        }
        public static uint Crc32(string value)
        {
            uint crc = 0xFFFFFFFF;
            foreach (byte b in Encoding.ASCII.GetBytes(value))
            {
                crc ^= b;
                for (int j = 0; j < 8; j++) crc = (crc >> 1) ^ ((crc & 1) != 0 ? 0xEDB88320u : 0);
            }
            return ~crc;
        }
    }
}
