using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace G4Composition
{
    // clHCA implements HCA v3's spectral reconstruction. Each request owns its decoder state.
    public static class HcaVoiceDecoder
    {
        public sealed class Audio
        {
            public int SampleRate, Channels;
            public float[] Samples;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Header
        {
            public uint Version, HeaderSize, SampleRate, Channels, BlockSize, BlockCount;
            public uint EncoderDelay, EncoderPadding, LoopEnabled, LoopStartBlock, LoopEndBlock;
            public uint LoopStartDelay, LoopEndPadding, SamplesPerBlock;
            public IntPtr Comment;
            public uint EncryptionEnabled, SampleCount, LoopStartSample, LoopEndSample;
            public int Ambisonics;
        }

        const string Library = "AvatarHcaDecoder";
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern IntPtr clHCA_new();
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern void clHCA_delete(IntPtr decoder);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int clHCA_DecodeHeader(IntPtr decoder, byte[] data, uint length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int clHCA_getInfo(IntPtr decoder, out Header header);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern int clHCA_DecodeBlock(IntPtr decoder, [In, Out] byte[] data, uint length);
        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)] static extern void clHCA_ReadSamples(IntPtr decoder, [Out] float[] samples);

        public static Audio Decode(byte[] data, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            IntPtr decoder = clHCA_new();
            if (decoder == IntPtr.Zero) throw new OutOfMemoryException("無法建立 HCA 解碼器。");
            try
            {
                if (clHCA_DecodeHeader(decoder, data, checked((uint)data.Length)) < 0 || clHCA_getInfo(decoder, out Header header) < 0)
                    throw new InvalidDataException("HCA 語音標頭或校驗碼無效。");
                if (header.EncryptionEnabled != 0) throw new InvalidDataException("此 HCA 語音需要加密金鑰，無法直接預覽。");
                long totalFrames = (long)header.BlockCount * header.SamplesPerBlock;
                long frames = totalFrames - header.EncoderDelay - header.EncoderPadding;
                if (header.Channels < 1 || header.Channels > 8 || header.SampleRate < 8000 || header.SampleRate > 192000 ||
                    header.SamplesPerBlock != 1024 || header.BlockSize == 0 || frames <= 0 || header.HeaderSize < 8 ||
                    header.HeaderSize + (long)header.BlockCount * header.BlockSize > data.Length)
                    throw new InvalidDataException("HCA 語音長度或取樣格式無效。");

                int channels = (int)header.Channels;
                int outputFrames = checked((int)Math.Min(frames, 30L * header.SampleRate));
                var samples = new float[checked(outputFrames * channels)];
                var frame = new float[checked((int)header.SamplesPerBlock * channels)];
                var encoded = new byte[header.BlockSize];
                int written = 0;
                for (int i = 0; i < header.BlockCount && written < samples.Length; i++)
                {
                    token.ThrowIfCancellationRequested();
                    Buffer.BlockCopy(data, checked((int)(header.HeaderSize + (long)i * header.BlockSize)), encoded, 0, encoded.Length);
                    // Successful decoding returns the number of bits consumed (not necessarily zero).
                    int status = clHCA_DecodeBlock(decoder, encoded, header.BlockSize);
                    if (status < 0) throw new InvalidDataException("HCA 語音資料損毀，區塊 " + i + "，錯誤 " + status + "。");
                    clHCA_ReadSamples(decoder, frame);
                    int skip = checked((int)Math.Max(0, (long)header.EncoderDelay - (long)i * header.SamplesPerBlock));
                    if (skip >= header.SamplesPerBlock) continue;
                    int count = Math.Min(frame.Length - skip * channels, samples.Length - written);
                    Array.Copy(frame, skip * channels, samples, written, count);
                    written += count;
                }
                if (written != samples.Length) throw new InvalidDataException("HCA 語音資料不完整。");
                // Avoid clipping without changing quiet recordings or filtering the voice.
                float peak = 0;
                foreach (float sample in samples)
                {
                    if (float.IsNaN(sample) || float.IsInfinity(sample)) throw new InvalidDataException("HCA 解碼結果無效。");
                    peak = Math.Max(peak, Math.Abs(sample));
                }
                if (peak > .98f)
                {
                    float gain = .98f / peak;
                    for (int i = 0; i < samples.Length; i++) samples[i] *= gain;
                }
                return new Audio { Samples = samples, SampleRate = (int)header.SampleRate, Channels = channels };
            }
            finally { clHCA_delete(decoder); }
        }
    }
}
