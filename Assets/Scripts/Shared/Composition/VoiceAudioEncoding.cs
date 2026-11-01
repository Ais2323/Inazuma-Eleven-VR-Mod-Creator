using System;
using System.IO;
using VGAudio.Containers.Hca;

namespace G4Composition
{
    public static class VoiceAudioEncoding
    {
        // Native character voices are mono. Keep the supplied duration, never stretch to another character's clip.
        public static byte[] Encode(float[] samples, int rate, int channels)
        {
            if (rate < 1 || channels < 1 || samples.Length % channels != 0) throw new InvalidDataException("Invalid voice PCM format.");
            int frames = samples.Length / channels;
            int count = checked((int)Math.Ceiling(frames * 48000.0 / rate));
            if (count < 1 || count > 30 * 48000) throw new InvalidDataException("Invalid voice duration (maximum 30 seconds).");
            var pcm = new double[count];
            for (int i = 0; i < count; i++)
            {
                double position = i * (double)rate / 48000;
                int a = Math.Min((int)position, frames - 1), b = Math.Min(a + 1, frames - 1);
                double value = 0, fraction = position - a;
                for (int c = 0; c < channels; c++) value += samples[a * channels + c] * (1 - fraction) + samples[b * channels + c] * fraction;
                value /= channels;
                if (double.IsNaN(value) || double.IsInfinity(value)) throw new InvalidDataException("Invalid voice samples.");
                pcm[i] = Math.Max(-1, Math.Min(1, value));
            }
            return Hca3Encoder.Encode(pcm);
        }

        // Old .charamaker files contain VGAudio HCA 2.0. Convert a copy at export;
        // keep the save and the user's amplitude unchanged. HCA 3.0 passes through.
        public static byte[] ToGameFormat(byte[] hca)
        {
            CustomVoiceBank.HcaInfo(hca);
            int version = hca[4] << 8 | hca[5];
            if (version == 0x300) return hca;
            if (version != 0x200) throw new InvalidDataException("Unsupported uploaded HCA version.");
            var pcm = new HcaReader().ReadFormat(hca).ToPcm16();
            var samples = new float[checked(pcm.SampleCount * pcm.Channels.Length)];
            for (int i = 0; i < pcm.SampleCount; i++)
                for (int c = 0; c < pcm.Channels.Length; c++) samples[i * pcm.Channels.Length + c] = pcm.Channels[c][i] / 32768f;
            return Encode(samples, pcm.SampleRate, pcm.Channels.Length);
        }
    }
}
