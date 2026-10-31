using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEngine;

namespace G4Composition
{
    // One independent decision per game grid cell. Templates contain glyphs,
    // never complete character codes or character-specific settings.
    public sealed class AvatarRecipeGlyphMatcher
    {
        const int Width = 32, Height = 40, Words = Width * Height / 64;
        [Serializable] sealed class Data { public Sample[] samples = null; }
        [Serializable] sealed class Sample { public string character = null; public string[] bits = null; }
        sealed class Template { public char Character; public ulong[][] Bits; public int[] Counts; }
        readonly Template[] templates;

        public AvatarRecipeGlyphMatcher(string json)
        {
            var data = JsonUtility.FromJson<Data>(json);
            if (data?.samples == null) throw new InvalidDataException("缺少角色編號字形資料。");
            templates = data.samples.Select(sample =>
            {
                if (sample.character?.Length != 1 || sample.bits?.Length != 3) throw new InvalidDataException("角色編號字形格式錯誤。");
                var layers = sample.bits.Select(encoded =>
                {
                    byte[] bytes = Convert.FromBase64String(encoded);
                    if (bytes.Length != Words * 8) throw new InvalidDataException("角色編號字形尺寸錯誤。");
                    var words = new ulong[Words]; Buffer.BlockCopy(bytes, 0, words, 0, bytes.Length); return words;
                }).ToArray();
                return new Template { Character = sample.character[0], Bits = layers, Counts = layers.Select(layer => layer.Sum(PopCount)).ToArray() };
            }).ToArray();
        }

        public string Recognize(string[] glyphs, CancellationToken cancellation, out string error)
        {
            if (glyphs?.Length != 77) throw new InvalidDataException("找不到完整的 77 格角色編號。");
            var code = new char[77]; var problems = new List<string>();
            for (int cell = 0; cell < code.Length; cell++)
            {
                cancellation.ThrowIfCancellationRequested();
                byte[] gray = Convert.FromBase64String(glyphs[cell]);
                if (gray.Length != Width * Height) throw new InvalidDataException("角色編號格子尺寸錯誤。");
                var sorted = (byte[])gray.Clone(); Array.Sort(sorted);
                int background = sorted[sorted.Length / 2], contrast = sorted[sorted.Length - 1] - background;
                if (contrast < 35) { code[cell] = '?'; problems.Add(Location(cell) + "沒有清楚字元"); continue; }
                var layers = new ulong[3][]; var counts = new int[3];
                for (int level = 0; level < 3; level++)
                {
                    layers[level] = new ulong[Words]; double threshold = background + contrast * (.25 + .2 * level);
                    for (int i = 0; i < gray.Length; i++) if (gray[i] > threshold) { layers[level][i / 64] |= 1UL << (i % 64); counts[level]++; }
                }
                var shifted = new ulong[9][][]; int variant = 0;
                for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++)
                {
                    shifted[variant] = new ulong[3][];
                    for (int level = 0; level < 3; level++)
                    {
                        var bits = new ulong[Words];
                        for (int y = 0; y < Height; y++) for (int x = 0; x < Width; x++)
                        {
                            int i = y * Width + x, nx = x + dx, ny = y + dy;
                            if (nx < 0 || nx >= Width || ny < 0 || ny >= Height || (layers[level][i / 64] & (1UL << (i % 64))) == 0) continue;
                            int target = ny * Width + nx; bits[target / 64] |= 1UL << (target % 64);
                        }
                        shifted[variant][level] = bits;
                    }
                    variant++;
                }
                var scores = new Dictionary<char, double>();
                foreach (Template template in templates)
                {
                    double best = 1;
                    foreach (var raster in shifted)
                    {
                        double score = 0;
                        for (int level = 0; level < 3; level++)
                        {
                            int difference = 0;
                            for (int w = 0; w < Words; w++) difference += PopCount(raster[level][w] ^ template.Bits[level][w]);
                            score += difference / (double)Math.Max(1, counts[level] + template.Counts[level]);
                        }
                        best = Math.Min(best, score / 3);
                    }
                    if (!scores.TryGetValue(template.Character, out double previous) || best < previous) scores[template.Character] = best;
                }
                var ranked = scores.OrderBy(pair => pair.Value).Take(2).ToArray(); code[cell] = ranked[0].Key;
                if (ranked[0].Value > .36 || ranked[1].Value - ranked[0].Value < .018)
                    problems.Add(Location(cell) + "無法確認「" + ranked[0].Key + "／" + ranked[1].Key + "」");
            }
            error = problems.Count == 0 ? "" : string.Join("；", problems.Take(6)) + (problems.Count > 6 ? "…（共 " + problems.Count + " 格）" : "") + "。請使用清楚的完整截圖，辨識結果已保留。";
            return new string(code);
        }

        static string Location(int cell) => "第 " + (cell / 20 + 1) + " 排第 " + (cell % 20 + 1) + " 格";
        static int PopCount(ulong value)
        {
            value -= (value >> 1) & 0x5555555555555555UL;
            value = (value & 0x3333333333333333UL) + ((value >> 2) & 0x3333333333333333UL);
            return (int)((((value + (value >> 4)) & 0x0F0F0F0F0F0F0F0FUL) * 0x0101010101010101UL) >> 56);
        }
    }
}