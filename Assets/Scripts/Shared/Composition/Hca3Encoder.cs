using System;
using System.IO;

namespace G4Composition
{
    // Mono HCA 3.0, 48 kHz. No gain adjustment, normalization,
    // dynamic-range compression or time stretching.
    // Follows the HCA-v3 reference algorithm inspected in Voci_Editor.
    internal static class Hca3Encoder
    {
        const int Bands = 128, FrameBytes = 341, Delay = 128, Offset = 2048;
        static readonly double[] Window = Floats("3a3504f03b0183b83b70c5383bbb92683c04a8093c3082003c61284c3c8b3f173ca839923cc77fbd3ce911103d0677cd3d198fc43d2dd35c3d4346433d59ecc13d71cba83d85741e3d92a4133da078b43daef5223dbe1c9e3dcdf27b3dde7a1d3defb6ed3e00d62b3e0a2eda3e13e72a3e1e00b13e287cf23e335d553e3ea3213e4a4f753e56633f3e62df373e6fc3d13e7d11383e8563a23e8c72b73e93b5613e9b2aef3ea2d26f3eaaaaab3eb2b2223ebae7063ec347373ecbd03d3ed47f463edd51283ee6425c3eef4eff3ef872d73f00d4a93f0576ca3f0a1d3b3f0ec5483f136c253f180ef23f1caac23f213ca23f25c1a53f2a36e73f2e99983f32e705bf371c9ebf3b37febf3f36f2bf431780bf46d7e6bf4a76a4bf4df27cbf514a6fbf547dc5bf578c03bf5a74eebf5d3887bf5fd707bf6250dabf64a699bf66d908bf68e90ebf6ad7b1bf6ca611bf6e5562bf6fe6e7bf715befbf72b5d1bf73f5e6bf751d89bf762e13bf7728d7bf780f20bf78e234bf79a34cbf7a5397bf7af439bf7b8648bf7c0acebf7c82c8bf7cef26bf7d50cbbf7da88ebf7df737bf7e3d86bf7e7c2abf7eb3ccbf7ee507bf7f106cbf7f3683bf7f57cabf7f74b6bf7f8db6bf7fa32ebf7fb57bbf7fc4f6bf7fd1edbf7fdcadbf7fe579bf7fec90bf7ff22ebf7ff688bf7ff9d0bf7ffc32bf7ffddabf7ffeedbf7fff8fbf7fffdfbf7ffffc"), Scale = Floats("342a8d2634633f893497657d34c9b9be35066491353311c4356e9910359ef53235d3ccf1360d1adf363c034a367a83b336a6e59536de60f5371426ff3745672a3783835937af3b7937e97c38381b8d3a384f4319388a14d538b7fbf038f5257d3923520f39599d163990fa4d39c12c4d3a00b1ed3a2b7a3a3a647b6d3a9837f03acad2263b071f623b340aaf3b6fe4ba3b9fd2283bd4f35b3c0ddf043c3d08a43c7bdfed3ca7cd943cdf96133d14f4f03d4679913d843a293db02f0e3deac0c73e1c65733e5063343e8ad4c63eb8fbaf3ef67a413f2435163f5acb943f91c3d33fc238d2400164d2402c68974065b90740990b8840cbec154107db35413504f3"), Range = Floats("3f8000003f2aaaab3ecccccd3e9249253e638e393e3a2e8c3e1d89d93e0888893d8421083d0208213c8102043c0080813b8040203b0020083a8010023a000801");
        static readonly byte[] Inverse = Hex("0e0e0e0e0e0e0d0d0d0d0d0d0c0c0c0c0c0c0b0b0b0b0b0b0a0a0a0a0a0a0a0909090909090808080808080706060504040403030302020202010101010101010101"), MaxBits = Hex("000203030404040405060708090a0b0c"), ReadBits = Hex("0000000000000000000000000000000001010202000000000000000000000000020202020202030300000000000000000202030303030303000000000000000003030303030303030303030303030404030303030303030303030404040404040303030303030404040404040404040403030404040404040404040404040404"), ReadValues = Hex("00000000000000000000000000000000000001ff00000000000000000000000000000101ffff02fe0000000000000000000001ff02fe03fd000000000000000000000101ffff0202fefe0303fdfd04fc00000101ffff0202fefe03fd04fc05fb00000101ffff02fe03fd04fc05fb06fa000001ff02fe03fd04fc05fb06fa07f9");
        static readonly double[,] Transform = MakeTransform();
        static readonly int[,] CodeBits = new int[16, 4097], CodeValues = new int[16, 4097];
        static readonly int[] Limits = new int[16];

        static Hca3Encoder()
        {
            for (int r = 1; r < 16; r++)
            {
                int width = MaxBits[r];
                if (r <= 7)
                {
                    for (int code = 0; code < (1 << width); code++)
                    {
                        int value = unchecked((sbyte)ReadValues[r * 16 + code]);
                        int bits = ReadBits[r * 16 + code];
                        if (CodeBits[r, value + Offset] != 0) continue;
                        CodeBits[r, value + Offset] = bits;
                        CodeValues[r, value + Offset] = code >> (width - bits);
                        Limits[r] = Math.Max(Limits[r], value);
                    }
                }
                else
                {
                    Limits[r] = (1 << (width - 1)) - 1;
                    for (int v = -Limits[r]; v <= Limits[r]; v++)
                    {
                        CodeBits[r, v + Offset] = v == 0 ? width - 1 : width;
                        CodeValues[r, v + Offset] = v == 0 ? 0 : (Math.Abs(v) << 1) | (v < 0 ? 1 : 0);
                    }
                }
            }
        }

        internal static byte[] Encode(double[] samples)
        {
            if (samples == null || samples.Length < 1 || samples.Length > 30 * 48000)
                throw new InvalidDataException("Invalid HCA voice duration (maximum 30 seconds).");
            int frames = (Delay + samples.Length + 1023) / 1024;
            var padded = new double[(frames * 8 + 1) * Bands];
            Array.Copy(samples, 0, padded, Delay, samples.Length);
            var output = new byte[96 + frames * FrameBytes];
            var x = new double[8, Bands]; var peaks = new double[Bands]; var sf = new int[Bands];
            var resolutions = new int[Bands]; var quantized = new int[8, Bands];
            const int budget = FrameBytes * 8 - 48;
            for (int frame = 0; frame < frames; frame++)
            {
                double largest = 0;
                for (int band = 0; band < Bands; band++)
                {
                    double peak = 0;
                    for (int sub = 0; sub < 8; sub++)
                    {
                        int start = (frame * 8 + sub) * Bands;
                        double sum = 0;
                        for (int i = 0; i < 256; i++) sum += padded[start + i] * Transform[i, band];
                        x[sub, band] = sum; peak = Math.Max(peak, Math.Abs(sum));
                    }
                    peaks[band] = peak; largest = Math.Max(largest, peak);
                    int index = 0; while (index < 63 && Scale[index] < peak) index++;
                    sf[band] = index;
                }
                for (int i = 0; i < Bands; i++) if (peaks[i] < Math.Max(Scale[0], largest * 1e-4)) sf[i] = 0;
                int noise, diffBits;
                while (true)
                {
                    int scaleBits = ScaleBits(sf, out diffBits);
                    int low = 0, high = 511 * 128;
                    if (Plan(x, sf, Noise(high), resolutions, quantized) + scaleBits <= budget)
                    {
                        while (low < high)
                        {
                            int middle = (low + high) / 2;
                            if (Plan(x, sf, Noise(middle), resolutions, quantized) + scaleBits <= budget) high = middle;
                            else low = middle + 1;
                        }
                        noise = Noise(high); break;
                    }
                    int nonzero = 0;
                    for (int i = 0; i < Bands; i++) if (sf[i] != 0) nonzero++;
                    if (nonzero == 0) throw new InvalidDataException("Cannot fit HCA voice frame.");
                    int remove = Math.Max(1, nonzero / 16);
                    for (int i = 0; i < remove; i++)
                    {
                        int weakest = -1;
                        for (int j = 0; j < Bands; j++)
                            if (sf[j] != 0 && (weakest < 0 || peaks[j] < peaks[weakest])) weakest = j;
                        sf[weakest] = 0;
                    }
                }
                Plan(x, sf, noise, resolutions, quantized);
                int at = 96 + frame * FrameBytes;
                var bitsOut = new Bits(output, at, at + FrameBytes - 2);
                int level = (noise + 255) / 256;
                bitsOut.Put(65535, 16); bitsOut.Put(level, 9); bitsOut.Put(level * 256 - noise, 7);
                bitsOut.Put(diffBits, 3);
                if (diffBits != 0)
                {
                    bitsOut.Put(sf[0], 6); int previous = sf[0], escape = (1 << diffBits) - 1;
                    for (int i = 1; i < Bands; i++)
                    {
                        int difference = sf[i] - previous + (escape >> 1);
                        if (diffBits >= 6) bitsOut.Put(sf[i], 6);
                        else if (difference >= 0 && difference < escape) bitsOut.Put(difference, diffBits);
                        else { bitsOut.Put(escape, diffBits); bitsOut.Put(sf[i], 6); }
                        previous = sf[i];
                    }
                }
                for (int sub = 0; sub < 8; sub++) for (int i = 0; i < Bands; i++)
                    if (resolutions[i] != 0)
                    {
                        int q = quantized[sub, i] + Offset, r = resolutions[i];
                        bitsOut.Put(CodeValues[r, q], CodeBits[r, q]);
                    }
                Write16(output, at + FrameBytes - 2, Crc(output, at, FrameBytes - 2));
            }
            WriteAscii(output, 0, "HCA\0"); Write16(output, 4, 0x300); Write16(output, 6, 96);
            WriteAscii(output, 8, "fmt\0"); Write32(output, 12, (1 << 24) | 48000); Write32(output, 16, frames);
            Write16(output, 20, Delay); Write16(output, 22, frames * 1024 - Delay - samples.Length);
            WriteAscii(output, 24, "comp"); Write16(output, 28, FrameBytes);
            byte[] comp = { 1, 15, 1, 1, 128, 128, 0, 0, 0, 0 }; Array.Copy(comp, 0, output, 30, comp.Length);
            WriteAscii(output, 40, "ciph"); WriteAscii(output, 46, "pad\0");
            Write16(output, 94, Crc(output, 0, 94));
            return output;
        }

        static int Noise(int index) => index == 0 ? 0 : ((index - 1) / 128 + 1) * 256 - (127 - (index - 1) % 128);
        static int Plan(double[,] x, int[] sf, int noise, int[] resolutions, int[,] quantized)
        {
            int bits = 0;
            for (int i = 0; i < Bands; i++)
            {
                int position = ((noise + i) >> 8) + 1 - ((5 * sf[i]) >> 1);
                int resolution = position < 0 ? 15 : position <= 65 ? Inverse[position] : 0;
                resolution = sf[i] == 0 ? 0 : Math.Max(1, Math.Min(15, resolution));
                resolutions[i] = resolution;
                double gain = Scale[sf[i]] * Range[resolution]; int limit = Limits[resolution];
                for (int sub = 0; sub < 8; sub++)
                {
                    int q = resolution == 0 ? 0 : (int)Math.Max(-limit, Math.Min(limit, Math.Round(x[sub, i] / gain)));
                    quantized[sub, i] = q;
                    if (resolution != 0) bits += CodeBits[resolution, q + Offset];
                }
            }
            return bits;
        }
        static int ScaleBits(int[] sf, out int diff)
        {
            bool nonzero = false; foreach (int value in sf) nonzero |= value != 0;
            if (!nonzero) { diff = 0; return 3; }
            int best = 3 + 6 * Bands; diff = 6;
            for (int db = 1; db < 6; db++)
            {
                int escape = (1 << db) - 1, count = 9;
                for (int i = 1; i < Bands; i++)
                { int d = sf[i] - sf[i - 1] + (escape >> 1); count += d >= 0 && d < escape ? db : db + 6; }
                if (count < best) { best = count; diff = db; }
            }
            return best;
        }
        static double[,] MakeTransform()
        {
            var result = new double[256, Bands];
            double D(int row, int col) => .125 * Math.Cos(Math.PI / Bands * (row + .5) * (col + .5));
            for (int i = 0; i < 64; i++) for (int k = 0; k < Bands; k++)
            {
                result[i, k] = Window[i] * D(i + 64, k);
                result[i + 64, k] = Window[i + 64] * D(127 - i, k);
                result[i + 128, k] = Window[127 - i] * D(63 - i, k);
                result[i + 192, k] = -Window[63 - i] * D(i, k);
            }
            return result;
        }
        sealed class Bits
        {
            readonly byte[] output; readonly int end; int position;
            internal Bits(byte[] output, int start, int end) { this.output = output; position = start * 8; this.end = end * 8; }
            internal void Put(int value, int count)
            {
                if (position + count > end) throw new InvalidDataException("HCA voice frame overflow.");
                for (int i = count - 1; i >= 0; i--, position++)
                    output[position >> 3] |= (byte)(((value >> i) & 1) << (7 - (position & 7)));
            }
        }
        static byte[] Hex(string value)
        { var bytes = new byte[value.Length / 2]; for (int i = 0; i < bytes.Length; i++) bytes[i] = Convert.ToByte(value.Substring(i * 2, 2), 16); return bytes; }
        static double[] Floats(string value)
        {
            byte[] bytes = Hex(value); var values = new double[bytes.Length / 4];
            for (int i = 0; i < values.Length; i++)
            { byte[] word = new byte[4]; Array.Copy(bytes, i * 4, word, 0, 4); if (BitConverter.IsLittleEndian) Array.Reverse(word); values[i] = BitConverter.ToSingle(word, 0); }
            return values;
        }
        static int Crc(byte[] bytes, int start, int length)
        {
            int crc = 0;
            for (int i = start; i < start + length; i++)
            { crc ^= bytes[i] << 8; for (int bit = 0; bit < 8; bit++) crc = ((crc << 1) ^ ((crc & 0x8000) != 0 ? 0x8005 : 0)) & 65535; }
            return crc;
        }
        static void WriteAscii(byte[] bytes, int at, string value) { for (int i = 0; i < value.Length; i++) bytes[at + i] = (byte)value[i]; }
        static void Write16(byte[] bytes, int at, int value) { bytes[at] = (byte)(value >> 8); bytes[at + 1] = (byte)value; }
        static void Write32(byte[] bytes, int at, int value) { Write16(bytes, at, value >> 16); Write16(bytes, at + 2, value); }
    }
}
