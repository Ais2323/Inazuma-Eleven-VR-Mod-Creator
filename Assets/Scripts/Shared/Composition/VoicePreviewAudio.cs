using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace G4Composition
{
    // Decode one stream from the selected character's AFS2 bank, without extracting the bank to disk.
    public static class VoicePreviewAudio
    {
        public sealed class Result
        {
            public string Bank;
            public int WaveId, SampleRate, Channels;
            public float[] Samples;
        }
        struct Wave { public long Start; public int Length, Id; }

        public static string ResolveBank(string commonPath, string characterId, string avatarVoice)
        {
            if (string.IsNullOrWhiteSpace(commonPath)) throw new InvalidOperationException("請先載入來源檔。");
            string name = avatarVoice;
            if (string.IsNullOrWhiteSpace(name))
            {
                if (!SafeName(characterId)) throw new InvalidDataException("尚未選擇語音人物。");
                string directory = Path.Combine(commonPath, "gamedata", "character");
                string tablePath = Directory.GetFiles(directory, "chara_base_*.cfg.bin").Single();
                var rows = CfgTable.Read(File.ReadAllBytes(tablePath), tablePath).Rows
                    .Where(r => r.Name == "CHARA_BASE_INFO" && r.Values.Length > 9).ToArray();
                var row = rows.FirstOrDefault(r => r.Text(1) == characterId);
                var visited = new HashSet<uint>();
                name = characterId;
                while (row != null && row.UInt(9) != 0)
                {
                    uint reference = row.UInt(9);
                    if (!visited.Add(reference)) throw new InvalidDataException("語音人物的參照循環。");
                    var source = rows.FirstOrDefault(r => r.UInt(0) == reference);
                    if (source == null)
                    {
                        name = AvatarRecipeParser.ResolveGenericVoiceName(commonPath, reference);
                        if (name == null) throw new InvalidDataException("找不到人物使用的語音設定：" + characterId);
                        break;
                    }
                    name = source.Text(1);
                    row = source;
                }
            }
            if (!SafeName(name)) throw new InvalidDataException("無效的語音音庫名稱。");
            string path = Path.Combine(commonPath, "sound_asset", "ja", name + ".awb");
            if (!File.Exists(path)) throw new FileNotFoundException("找不到此人物的日文語音音庫：" + name, path);
            return path;
        }

        static bool SafeName(string name) => !string.IsNullOrWhiteSpace(name) &&
            name.All(c => char.IsLetterOrDigit(c) || c == '_');

        public static Result Decode(string bank, int random, int previousWave, CancellationToken token)
        {
            byte[] encoded;
            int selectedId;
            using (var stream = File.OpenRead(bank))
            using (var reader = new BinaryReader(stream))
            {
                if (new string(reader.ReadChars(4)) != "AFS2") throw new InvalidDataException("不支援的語音音庫格式。");
                reader.ReadByte(); int offsetBytes = reader.ReadByte(), idBytes = reader.ReadByte(); reader.ReadByte();
                uint count = reader.ReadUInt32(); int alignment = reader.ReadUInt16(); reader.ReadUInt16();
                if (count == 0 || count > 65536 || alignment == 0 || offsetBytes < 1 || offsetBytes > 8 || idBytes < 1 || idBytes > 4)
                    throw new InvalidDataException("語音音庫索引無效。");
                var ids = new int[count];
                for (int i = 0; i < count; i++) ids[i] = checked((int)ReadUnsigned(reader, idBytes));
                var offsets = new long[count + 1];
                for (int i = 0; i <= count; i++) offsets[i] = checked((long)ReadUnsigned(reader, offsetBytes));
                long tableEnd = stream.Position;
                var waves = new List<Wave>();
                for (int i = 0; i < count; i++)
                {
                    token.ThrowIfCancellationRequested();
                    long start = checked((offsets[i] + alignment - 1) / alignment * alignment);
                    long length = offsets[i + 1] - start;
                    if (start < tableEnd || length < 4 || length > 16 * 1024 * 1024 || start > stream.Length - length)
                        throw new InvalidDataException("語音音庫資料超出範圍。");
                    stream.Position = start;
                    if ((reader.ReadByte() & 127) != 'H' || (reader.ReadByte() & 127) != 'C' || (reader.ReadByte() & 127) != 'A' || reader.ReadByte() != 0)
                        continue;
                    waves.Add(new Wave { Start = start, Length = (int)length, Id = ids[i] });
                }
                if (waves.Count == 0) throw new InvalidDataException("此音庫沒有可播放的 HCA 語音。");
                var choices = waves.Count > 1 ? waves.Where(w => w.Id != previousWave).ToList() : waves;
                var selected = choices[(int)((uint)random % (uint)choices.Count)];
                selectedId = selected.Id;
                stream.Position = selected.Start;
                encoded = reader.ReadBytes(selected.Length);
            }
            var audio = HcaVoiceDecoder.Decode(encoded, token);
            return new Result { Bank = bank, WaveId = selectedId, Samples = audio.Samples, SampleRate = audio.SampleRate, Channels = audio.Channels };
        }

        static ulong ReadUnsigned(BinaryReader reader, int bytes)
        {
            ulong value = 0;
            for (int i = 0; i < bytes; i++) value |= (ulong)reader.ReadByte() << (i * 8);
            return value;
        }

    }
}
