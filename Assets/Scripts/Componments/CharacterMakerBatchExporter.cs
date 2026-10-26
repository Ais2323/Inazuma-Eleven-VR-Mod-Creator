using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using G4Composition;
using G4Runtime;
using UnityEngine;

public sealed class CharacterMakerBatchExporter
{
    private static readonly string[] SeriesNames =
    {
        "20_EDIT", "01_IE1", "02_IE2", "03_IE3", "04_GO1",
        "05_GO2", "06_GO3", "07_ARES", "08_ORION", "11_VICTORY"
    };

    public sealed class BuiltCharacter
    {
        public CharacterMakerDocument Document;
        public string TargetRelativePath;
        public byte[] Metadata;
        public byte[] Geometry;
        public byte[] Textures;
        public byte[] Portrait;
    }

    public BuiltCharacter Build(CharacterMakerDocument saved)
    {
        ValidateDocument(saved);
        // Migration and source resolution must not modify the caller's save.
        saved = JsonUtility.FromJson<CharacterMakerDocument>(JsonUtility.ToJson(saved));
        string targetPath = FindFaceModel(saved.targetId);
        RawHeadAsset target = Read(targetPath);
        var avatars = new Dictionary<string, RawHeadAsset>(StringComparer.Ordinal);
        RawHeadAsset ResolveSource(CharacterMakerPart part)
        {
            if (string.IsNullOrWhiteSpace(part.avatarCode)) return Read(FindFaceModel(part.sourceId));
            var recipe = AvatarRecipeParser.Parse(part.avatarCode);
            if (part.sourceId != AvatarCompositionAsset.SourceId(recipe.Code)) throw new InvalidDataException("Avatar 編號與來源 ID 不符。");
            if (!avatars.TryGetValue(recipe.Code, out RawHeadAsset asset))
                avatars.Add(recipe.Code, asset = AvatarCompositionAsset.Build(recipe.Code));
            return asset;
        }
        RawHeadAsset head = ResolveSource(saved.head);
        RawHeadAsset hair = ResolveSource(saved.hair);
        RawHeadAsset other = string.IsNullOrWhiteSpace(saved.other.sourceId) ? null : ResolveSource(saved.other);

        Vector3 targetAnchor = ResolveTargetHeadAnchor(saved, target);
        ResolvePart(saved, saved.head, head, 1, targetAnchor);
        ResolvePart(saved, saved.hair, hair, 2, targetAnchor);
        if (other != null) ResolvePart(saved, saved.other, other, 3, targetAnchor);
        saved.uniformHem = saved.Hem;
        saved.version = 15;

        var sources = new Dictionary<string, RawHeadAsset>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in new[] { target, head, hair }) sources[source.RelativePath] = source;
        if (other != null) sources[other.RelativePath] = other;

        var composition = new CompositionDocument();
        AddParts(composition, hair, saved.hair);
        if (other != null) AddParts(composition, other, saved.other);
        AddParts(composition, head, saved.head);
        G4CompositionWriter.Result result = new G4CompositionWriter().Build(
            composition, sources, target, CancellationToken.None);
        var optionSources = new Dictionary<int, RawHeadAsset> { [1] = head, [2] = hair };
        if (other != null) optionSources[3] = other;
        result.Textures = ApplyTextures(saved, result.Metadata, result.Geometry, result.Textures, optionSources);

        byte[] portrait = null;
        if (!string.IsNullOrWhiteSpace(saved.portraitG4txBase64))
        {
            portrait = Convert.FromBase64String(saved.portraitG4txBase64);
            List<RawTexture> layers = RawTexture.Read(portrait, saved.targetId + " portrait");
            if (layers.Count != 2) throw new InvalidDataException(saved.targetId + " 的頭像不是兩層 G4TX。");
        }
        return new BuiltCharacter
        {
            Document = saved,
            TargetRelativePath = targetPath,
            Metadata = result.Metadata,
            Geometry = result.Geometry,
            Textures = result.Textures,
            Portrait = portrait
        };
    }

    public static void Write(BuiltCharacter built, string outputRoot)
    {
        string relativeFolder = Path.GetDirectoryName(built.TargetRelativePath);
        string common = Path.Combine(outputRoot, "common", "chr", "_face", relativeFolder);
        string dx11 = Path.Combine(outputRoot, "dx11", "chr", "_face", relativeFolder);
        Directory.CreateDirectory(common);
        Directory.CreateDirectory(dx11);
        File.WriteAllBytes(Path.Combine(common, built.Document.targetId + ".g4md"), built.Metadata);
        File.WriteAllBytes(Path.Combine(common, built.Document.targetId + ".g4mg"), built.Geometry);
        File.WriteAllBytes(Path.Combine(dx11, built.Document.targetId + ".g4tx"), built.Textures);
        if (built.Portrait != null)
        {
            string portrait = Path.Combine(outputRoot, "dx11", "menu", "200_icon", "10_icon_chr", "face");
            Directory.CreateDirectory(portrait);
            File.WriteAllBytes(Path.Combine(portrait, built.Document.targetId + "_l.g4tx"), RenamePortrait(built.Portrait, built.Document.targetId));
        }
    }

    public static byte[] RenamePortrait(byte[] container, string characterId)
    {
        if (string.IsNullOrWhiteSpace(characterId)) throw new InvalidDataException("Portrait character ID is missing.");
        var layers = RawTexture.Read(container, characterId + " portrait");
        if (layers.Count != 2) throw new InvalidDataException("Portrait must contain front and back layers.");
        string[] originalNames = layers.Select(t => t.Name).ToArray();
        bool changed = false;
        for (int i = 0; i < layers.Count; i++) {
            string suffix = "_" + (i + 1) + "_l00";
            if (!layers[i].Name.EndsWith(suffix, StringComparison.Ordinal))
                throw new InvalidDataException("Unsupported portrait layer name: " + layers[i].Name);
            string expected = characterId + suffix;
            if (layers[i].Name == expected) continue;
            layers[i].Name = expected; layers[i].Hash = G4TextureParser.Crc32(expected); changed = true;
        }
        if (!changed) return RawTexture.SortLookupIndices(container);
        if (layers.Select((t, i) => t.Name.Length == originalNames[i].Length).All(same => same)) {
            // Character IDs have equal width. Preserve all native padding, descriptors and DDS bytes.
            byte[] output = (byte[])container.Clone();
            int hashes = (BitConverter.ToUInt16(container, 4) + layers.Count * 48 + 15) & ~15;
            int names = (hashes + layers.Count * 5 + 3) & ~3;
            for (int i = 0; i < layers.Count; i++) {
                Buffer.BlockCopy(BitConverter.GetBytes(layers[i].Hash), 0, output, hashes + i * 4, 4);
                byte[] name = System.Text.Encoding.ASCII.GetBytes(layers[i].Name);
                Buffer.BlockCopy(name, 0, output, names + BitConverter.ToUInt16(container, names + i * 2), name.Length);
            }
            RawTexture.Read(output, characterId + " renamed portrait");
            return RawTexture.SortLookupIndices(output);
        }
        return RawTexture.Write(container, layers);
    }
    public static bool WriteMergedCharacterSettings(IEnumerable<CharacterMakerDocument> documents, string outputRoot, CharacterCatalog shared = null)
    {
        CharacterMakerDocument[] saved = documents.ToArray();
        if (saved.Length == 0) return false;
        string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
        string basePath = FindVersionedTable(directory, "chara_base_");
        string modelPath = FindVersionedTable(directory, "chara_model_");
        CfgTable bases = shared?.Base ?? CfgTable.Read(File.ReadAllBytes(basePath), basePath);
        CfgTable models = shared?.Models ?? CfgTable.Read(File.ReadAllBytes(modelPath), modelPath);
        var identityWriter = new CharacterIdentityWriter(bases, shared == null ? null : new[] { shared.Parameters, shared.Models }.Concat(shared.TextTables.Values));
        bool changed = false, iconsWritten = false;
        foreach (CharacterMakerDocument source in saved)
        {
            var document = JsonUtility.FromJson<CharacterMakerDocument>(JsonUtility.ToJson(source));
            document.UpgradeBodySettings(models);
            CfgTable.Row character = bases.Rows.SingleOrDefault(row => row.Name == "CHARA_BASE_INFO" &&
                row.Values.Length > 6 && string.Equals(row.Text(1), document.targetId, StringComparison.OrdinalIgnoreCase));
            if (character == null) throw new InvalidDataException(document.targetId + " 找不到 CHARA_BASE_INFO。");
            uint modelId = character.UInt(6);
            CfgTable.Row model = models.Rows.SingleOrDefault(row => row.Name == "CHARA_MODEL_INFO" &&
                row.Values.Length > 31 && row.UInt(0) == modelId);
            if (model == null) throw new InvalidDataException(document.targetId + " 找不到 CHARA_MODEL_INFO。");
            uint originalBodyId = model.UInt(4);
            CfgTable.Row originalBody = models.Rows.FirstOrDefault(row => IsBodyRow(row) && row.UInt(0) == originalBodyId);
            if (originalBody == null) throw new InvalidDataException(document.targetId + " 找不到原始 CHARA_BODY_INFO。");
            document.NormalizeBodyGender(models, document.identity?.gender ?? (int)character.Int(11), originalBody);
            CfgTable.Row replacement = NativeBodyTypes.Resolve(models, document.bodyType, document.bodyVariantId,
                originalBody.Int(5) >= 4, originalBody);
            if (replacement == null) throw new InvalidDataException(document.targetId + " 無法對應 Body Type " + document.bodyType + "。");
            int hem = document.Hem;
            uint skin = EncodeColor(document.skinColor);
            bool rowChanged = model.UInt(4) != replacement.UInt(0) || model.Int(31) != NativeBodyShape.ToNativeChestSize(document.chestSize) ||
                model.Int(12) != document.uniformCollar || model.Int(13) != (hem == 1 ? 1 : 0) || model.UInt(16) != skin;
            rowChanged |= identityWriter.Apply(document.identity, document.targetId, character, model);
            uint uniform = NativeUniformFashion.ExportModel(model.UInt(5), replacement, document.uniformSleeve, hem);
            rowChanged |= model.UInt(5) != uniform;
            iconsWritten |= NativeUniformFashion.WriteHemIconFallbacks(uniform, replacement, outputRoot);
            rowChanged |= identityWriter.ApplyFacial(document, model);
            identityWriter.ApplyVoice(document);
            if (!rowChanged) continue;
            // Resolve the style within the equipped kit, after any team equipment change.
            SetInteger(model, 5, uniform);
            SetInteger(model, 4, replacement.UInt(0));
            SetInteger(model, 31, NativeBodyShape.ToNativeChestSize(document.chestSize));
            SetInteger(model, 12, document.uniformCollar);
            SetInteger(model, 13, hem == 1 ? 1 : 0);
            SetInteger(model, 16, unchecked((int)skin));
            changed = true;
        }
        bool identityChanged = identityWriter.Write(outputRoot);
        if (!changed) return identityChanged || iconsWritten;
        byte[] bytes = models.Write();
        CfgTable.Read(bytes, "merged character settings validation");
        string relative = modelPath.Substring(DumpDataManager.CommonPath.Length + 1);
        string destination = Path.Combine(outputRoot, "common", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        File.WriteAllBytes(destination, bytes);
        return true;
    }

    private static void ValidateDocument(CharacterMakerDocument saved)
    {
        if (saved == null || saved.version < 1 || saved.version > 15) throw new InvalidDataException("不支援的存檔版本。");
        if (saved.uniformSleeve < 0 || saved.uniformSleeve > 1 || saved.Hem < 0 || saved.Hem > 2) throw new InvalidDataException("無效的袖子或下擺設定。");
        if (!SafeId(saved.targetId) || !SafeId(saved.head?.sourceId) || !SafeId(saved.hair?.sourceId))
            throw new InvalidDataException("目標、Head 或 Hair 的角色 ID 無效。");
        if (saved.other != null && !string.IsNullOrWhiteSpace(saved.other.sourceId) && !SafeId(saved.other.sourceId))
            throw new InvalidDataException("Other 的角色 ID 無效。");
        if (saved.bodyType < 1 || saved.bodyType > 8 || saved.chestSize < -1 || saved.chestSize > 2)
            throw new InvalidDataException("身體設定超出範圍。");
    }

    private static bool SafeId(string value) => !string.IsNullOrWhiteSpace(value) && Regex.IsMatch(value, @"^[A-Za-z0-9_]+$");
    private RawHeadAsset Read(string relative) => RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path, relative, CancellationToken.None);

    private static string FindFaceModel(string id)
    {
        if (!DumpDataManager.ValidateSavedPath()) throw new InvalidDataException("Dump 路徑無效：" + DumpDataManager.LastError);
        foreach (string series in SeriesNames)
        {
            string relative = Path.Combine(series, id, id + ".g4md");
            if (File.Exists(Path.Combine(DumpDataManager.CommonPath, "chr", "_face", relative))) return relative;
        }
        throw new FileNotFoundException("找不到角色模型：" + id);
    }

    private static void ResolvePart(CharacterMakerDocument document, CharacterMakerPart part, RawHeadAsset source, int option, Vector3 targetAnchor)
    {
        if (part == null) throw new InvalidDataException("缺少模型部位設定。");
        if (part.meshNames == null || part.meshNames.Count == 0)
        {
            IEnumerable<G4ModelParser.Part> selected = option == 1
                ? source.Preview.Parts.Where(value => IsFace(value.Name))
                : source.Preview.Parts.Where(value => !IsFace(value.Name)).Skip(option == 2 ? 0 : 1).Take(1);
            part.meshNames = selected.Select(value => value.Name).ToList();
        }
        if (part.meshNames.Count == 0) throw new InvalidDataException(source.RelativePath + " 找不到指定部位。");
        // exportPosition is a cache, not an independent user adjustment. Derive
        // it from the selected body's head bone after every load/export, so old
        // saves cannot restore a stale body-height baseline.
        Vector3 sourceAnchor = HeadAttachmentAnchor.Get(source);
        part.exportPosition = targetAnchor + part.position
            - Quaternion.Euler(part.rotation) * Vector3.Scale(sourceAnchor, part.scale);
        ValidateVector(part.exportPosition, false, "Position");
        ValidateVector(part.rotation, false, "Rotation");
        ValidateVector(part.scale, true, "Scale");
    }

    private static Vector3 ResolveTargetHeadAnchor(CharacterMakerDocument saved, RawHeadAsset target)
    {
        Vector3 anchor = HeadAttachmentAnchor.Get(target);
        string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
        string basesPath = FindVersionedTable(directory, "chara_base_");
        string modelsPath = FindVersionedTable(directory, "chara_model_");
        var bases = CfgTable.Read(File.ReadAllBytes(basesPath), basesPath);
        var models = CfgTable.Read(File.ReadAllBytes(modelsPath), modelsPath);
        var character = bases.Rows.Single(row => row.Name == "CHARA_BASE_INFO" &&
            row.Values.Length > 6 && string.Equals(row.Text(1), saved.targetId, StringComparison.OrdinalIgnoreCase));
        var model = models.Rows.Single(row => row.Name == "CHARA_MODEL_INFO" && row.UInt(0) == character.UInt(6));
        var original = models.Rows.First(row => IsBodyRow(row) && row.UInt(0) == model.UInt(4));
        saved.UpgradeBodySettings(models);
        saved.NormalizeBodyGender(models, saved.identity?.gender ?? (int)character.Int(11), original);
        var selected = NativeBodyTypes.Resolve(models, saved.bodyType, saved.bodyVariantId, original.Int(5) >= 4, original);
        return anchor + HeadAttachmentAnchor.GetBody(selected) - HeadAttachmentAnchor.GetBody(original);
    }

    private static void AddParts(CompositionDocument output, RawHeadAsset source, CharacterMakerPart saved)
    {
        var names = new HashSet<string>(saved.meshNames, StringComparer.OrdinalIgnoreCase);
        foreach (G4ModelParser.Part part in source.Preview.Parts.Where(value => names.Contains(value.Name)))
            output.Parts.Add(new CompositionPart
            {
                SourceRelativePath = source.RelativePath, MeshIndex = part.SourceMeshIndex, Name = part.Name,
                Position = saved.exportPosition, Rotation = saved.rotation, Scale = saved.scale,
                Triangles = Enumerable.Range(0, part.Indices.Length / 3).ToArray()
            });
    }

    private static byte[] ApplyTextures(CharacterMakerDocument saved, byte[] metadata, byte[] geometry, byte[] container,
        IReadOnlyDictionary<int, RawHeadAsset> sources)
    {
        G4ModelParser.Model model = G4ModelParser.Parse(metadata, geometry, "batch output");
        byte[] output = container;
        foreach (var entry in sources.OrderBy(value => value.Key))
        {
            int option = entry.Key;
            CharacterMakerPart savedPart = option == 1 ? saved.head : option == 2 ? saved.hair : saved.other;
            var names = new HashSet<string>(savedPart.meshNames, StringComparer.OrdinalIgnoreCase);
            var sourceHashes = new HashSet<uint>(entry.Value.Preview.Parts.Where(value => names.Contains(value.Name))
                .SelectMany(value => entry.Value.Preview.Materials[value.Material].TextureHashes));
            var destinationHashes = new HashSet<uint>(OutputParts(model, option)
                .SelectMany(value => model.Materials[value.Material].TextureHashes));
            foreach (string channel in new[] { "base", "oc", "spm" })
            {
                RawTexture[] candidates = entry.Value.Textures.Where(value => sourceHashes.Contains(value.Hash) && Channel(Role(value.Name)) == channel).ToArray();
                if (candidates.Length == 0) continue;
                if (candidates.Length != 1) throw new InvalidDataException("找到多張 " + channel + " 來源貼圖。");
                RawTexture sourceTexture = candidates[0];
                string role = Role(sourceTexture.Name);
                byte[] dds = sourceTexture.Dds;
                CharacterMakerTexture imported = saved.textures.FirstOrDefault(value => value.option == option &&
                    string.Equals(value.sourceId, savedPart.sourceId, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrWhiteSpace(savedPart.avatarCode) ? string.Equals(value.role, role, StringComparison.OrdinalIgnoreCase) : Channel(value.role) == channel));
                if (imported != null)
                {
                    dds = Convert.FromBase64String(imported.ddsBase64 ?? "");
                    if (option == 2 && !string.IsNullOrWhiteSpace(savedPart.avatarCode) && imported.avatarAtlasVersion < 1)
                    {
                        dds = CharacterManager.RepackLegacyAvatarHairDds(dds, entry.Value.LegacyHairAtlasBounds, channel != "base");
                        imported.ddsBase64 = Convert.ToBase64String(dds); imported.avatarAtlasVersion = 1;
                    }
                }
                try { output = ReplaceTexture(output, channel, dds, sourceTexture, destinationHashes); }
                catch (InvalidDataException exception)
                {
                    throw new InvalidDataException("部位 " + option + "（" + savedPart.sourceId + " / " + sourceTexture.Name + "）：" + exception.Message, exception);
                }
            }
        }
        return output;
    }

    private static IEnumerable<G4ModelParser.Part> OutputParts(G4ModelParser.Model model, int option)
    {
        if (option == 1) return model.Parts.Where(value => IsFace(value.Name));
        G4ModelParser.Part[] nonFace = model.Parts.Where(value => !IsFace(value.Name)).ToArray();
        int index = option == 2 ? 0 : 1;
        return index < nonFace.Length ? new[] { nonFace[index] } : Array.Empty<G4ModelParser.Part>();
    }

    private static byte[] ReplaceTexture(byte[] container, string channel, byte[] dds, RawTexture source, HashSet<uint> hashes)
    {
        List<RawTexture> textures = RawTexture.Read(container, "batch G4TX");
        RawTexture[] matches = textures.Where(value => hashes.Contains(value.Hash) && Channel(Role(value.Name)) == channel).ToArray();
        if (matches.Length != 1) throw new InvalidDataException("輸出 G4TX 找到 " + matches.Length + " 張 " + channel + " 貼圖。");
        // The conservative model template can come from a different character
        // (e.g. to supply an Other mesh). Its slot size is not the selected part's
        // texture contract. Replace both DDS and descriptor using the source.
        G4TextureParser.TextureData expected = G4TextureParser.ParseDDS(source.Dds, source.Name);
        G4TextureParser.TextureData imported = G4TextureParser.ParseDDS(dds, "batch imported " + channel);
        if (expected.Width != imported.Width || expected.Height != imported.Height || expected.MipCount != imported.MipCount)
            throw new InvalidDataException(channel + " DDS " + imported.Width + "x" + imported.Height + " / " + imported.MipCount +
                " mips does not match selected source " + source.Name + " " + expected.Width + "x" + expected.Height + " / " + expected.MipCount + " mips.");
        if (source.Descriptor == null || source.Descriptor.Length != 48 ||
            BitConverter.ToUInt16(source.Descriptor, 24) != imported.Width || BitConverter.ToUInt16(source.Descriptor, 26) != imported.Height)
            throw new InvalidDataException(channel + " DDS dimensions do not match its source G4TX descriptor.");
        matches[0].Dds = dds;
        matches[0].Descriptor = (byte[])source.Descriptor.Clone();
        return WriteG4TxPreservingTable(container, textures);
    }

    internal static byte[] WriteG4TxPreservingTable(byte[] template, IReadOnlyList<RawTexture> textures)
    {
        if (template == null || template.Length < 0x60)
            throw new InvalidDataException("G4TX template is too small.");
        int count = BitConverter.ToUInt16(template, 0x20);
        if (count != textures.Count)
            throw new InvalidDataException("G4TX texture count does not match its object table.");
        int payload = checked((0x60 + BitConverter.ToInt32(template, 12) + 15) & ~15);
        if (payload < 0x60 || payload > template.Length)
            throw new InvalidDataException("G4TX payload offset is invalid.");

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(template, 0, payload);
            for (int i = 0; i < textures.Count; i++)
            {
                while (stream.Position % 16 != 0) writer.Write((byte)0);
                int objectOffset = checked((int)stream.Position - payload);
                writer.Write(textures[i].Dds);
                int objectSize = textures[i].Dds.Length;
                long end = stream.Position;
                stream.Position = 0x60 + i * 48;
                writer.Write(textures[i].Descriptor);
                stream.Position = 0x64 + i * 48;
                writer.Write(objectOffset);
                writer.Write(objectSize);
                stream.Position = end;
            }
            while (stream.Position % 16 != 0) writer.Write((byte)0);
            int outputSize = checked((int)stream.Length);
            stream.Position = 0x2c;
            writer.Write(outputSize - payload);
            byte[] output = stream.ToArray();
            RawTexture.Read(output, "batch G4TX validation");
            return output;
        }
    }

    private static string Role(string name)
    {
        Match match = Regex.Match(name ?? "", @"_(\d{2})(oc|spm)?(?:\.\d+|\.a)?$", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Value + match.Groups[2].Value).ToLowerInvariant() : null;
    }
    private static string Channel(string role) => string.IsNullOrEmpty(role) ? null :
        role.EndsWith("spm", StringComparison.OrdinalIgnoreCase) ? "spm" :
        role.EndsWith("oc", StringComparison.OrdinalIgnoreCase) ? "oc" : Regex.IsMatch(role, @"^\d{2}$") ? "base" : null;
    private static bool IsFace(string name) => name.StartsWith("eye_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("mouth_", StringComparison.OrdinalIgnoreCase);

    private static void ValidateVector(Vector3 value, bool positive, string name)
    {
        if (!float.IsFinite(value.x) || !float.IsFinite(value.y) || !float.IsFinite(value.z) ||
            positive && (value.x <= 0 || value.y <= 0 || value.z <= 0))
            throw new InvalidDataException(name + " 無效。");
    }

    private static string FindVersionedTable(string directory, string prefix)
    {
        string[] paths = Directory.GetFiles(directory, prefix + "*.cfg.bin")
            .Where(path => Regex.IsMatch(Path.GetFileName(path), "^" + Regex.Escape(prefix) + @"\d")).ToArray();
        if (paths.Length != 1) throw new InvalidDataException("無法唯一判定資料表 " + prefix + "。");
        return paths[0];
    }
    private static bool IsBodyRow(CfgTable.Row row)
    {
        if (row?.Values == null || row.Values.Length < 7 || row.Types == null || row.Types.Length < 7) return false;
        string path = row.Text(2)?.Replace('\\', '/');
        return row.Types[0] == 1 && row.Types[2] == 0 && row.Types[5] == 1 && row.Types[6] == 1 &&
            !string.IsNullOrWhiteSpace(path) && path.StartsWith("_uniform/sh", StringComparison.OrdinalIgnoreCase);
    }
    private static void SetInteger(CfgTable.Row row, int index, long value)
    {
        if (row.Types[index] != 1) throw new InvalidDataException(row.Name + " 欄位不是整數。");
        row.Values[index] = value;
    }
    private static uint EncodeColor(Color color)
    {
        Color32 rgba = color;
        return ((uint)rgba.r << 24) | ((uint)rgba.g << 16) | ((uint)rgba.b << 8) | rgba.a;
    }
}
