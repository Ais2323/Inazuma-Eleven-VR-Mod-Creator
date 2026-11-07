using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;

public static partial class UniformMakerExporter
{
    static string[] WritePortraitFiles(UniformMakerVariant variant, CfgTable.Row body, uint sourceModel, uint targetModel,
        int sleeve, Dictionary<string, byte[]> converted, Dictionary<string, byte[]> pending, Dictionary<string, string[]> bindings)
    {
        int imageSleeve = variant.PortraitSleeve(sleeve);
        var source = NativeUniformFashion.Resolve(sourceModel, body, imageSleeve, 0) ?? NativeUniformFashion.Resolve(sourceModel, body, 0, 0);
        var fallback = NativeUniformFashion.Resolve(targetModel, body, imageSleeve, 0) ?? NativeUniformFashion.Resolve(targetModel, body, 0, 0);
        string textureMember = Path.GetFileNameWithoutExtension((source ?? fallback).UniformTexture).Split('_').Last();
        var images = new List<(UniformPortraitIcon Icon, byte[] Cloth, byte[] Mask)>();
        for (int collar = 0; collar < 2; collar++) {
            string nativeStem = UniformPortraitIcon.Find(source, body, variant.PortraitCollar(collar), imageSleeve) ?? UniformPortraitIcon.Find(fallback, body, variant.PortraitCollar(collar), imageSleeve);
            if (nativeStem == null) throw new FileNotFoundException("Missing uniform portrait for body " + body.Int(4));
            var icon = UniformPortraitIcon.Load(nativeStem);
            var custom = variant.portraitIcons.FirstOrDefault(p => p.collar == variant.PortraitCollar(collar) && p.sleeve == imageSleeve);
            string png = custom?.pngBase64;
            byte[] mask = icon.Mask.Dds;
            if (!string.IsNullOrEmpty(custom?.maskPngBase64)) {
                string identity = "portrait-mask|" + icon.Width + "|" + icon.Height + "|" + PayloadHash(custom.maskPngBase64);
                if (!converted.TryGetValue(identity, out mask)) converted.Add(identity, mask = icon.EncodeMask(custom.maskPngBase64));
            }
            byte[] cloth = icon.Cloth.Dds;
            if (!string.IsNullOrEmpty(png)) {
                string identity = "portrait|" + icon.Width + "|" + icon.Height + "|" + PayloadHash(png);
                if (!converted.TryGetValue(identity, out cloth)) converted.Add(identity, cloth = icon.EncodeCloth(png));
            }
            images.Add((icon, cloth, mask));
        }
        // Retail derives the body digits at offsets 5/6 of a seven-character model stem.
        // Share the binding across kit sides, roles and styles whenever both image layers match.
        string sourceStem = Path.GetFileNameWithoutExtension((source ?? fallback).Uniform);
        if (sourceStem.Length < 7) throw new InvalidDataException("Invalid native portrait model stem: " + sourceStem);
        string bodyDigits = sourceStem.Substring(5, 2);
        string bindingIdentity = bodyDigits + "|" + textureMember + "|" + string.Join("|", images.SelectMany(image => new[] {
            UniformMakerDocument.ContentDigest(Convert.ToBase64String(image.Cloth)),
            UniformMakerDocument.ContentDigest(Convert.ToBase64String(image.Mask)) }));
        if (bindings.TryGetValue(bindingIdentity, out var shared)) return shared;
        uint seed = G4Runtime.G4TextureParser.Crc32(bindingIdentity);
        string assetStem = null;
        for (int attempt = 0; attempt < 10000; attempt++) {
            string candidate = "u" + ((seed + (uint)attempt) % 10000).ToString("D4", System.Globalization.CultureInfo.InvariantCulture) + bodyDigits;
            string owner = candidate + "_" + textureMember;
            bool occupied = bindings.Values.Any(value => value[0] == candidate) ||
                Directory.GetFiles(Path.Combine(DumpDataManager.Dx11Path, UniformPortraitIcon.DirectoryPath), owner + "_*.g4tx").Length != 0 ||
                pending.Keys.Any(path => path.StartsWith("dx11/" + UniformPortraitIcon.DirectoryPath + "/" + owner + "_", StringComparison.OrdinalIgnoreCase));
            if (!occupied) { assetStem = candidate; break; }
        }
        if (assetStem == null) throw new InvalidDataException("No free native-format uniform portrait name.");
        string textureStem = assetStem + "_" + textureMember;
        for (int collar = 0; collar < 2; collar++) {
            var image = images[collar];
            for (int style = 0; style < 2; style++) {
                string stem = textureStem + "_" + style + collar;
                pending["dx11/" + UniformPortraitIcon.DirectoryPath + "/" + stem + "_l.g4tx"] = image.Icon.Write(stem, image.Cloth, image.Mask);
            }
            if (collar == 0) {
                string stem = assetStem + "_" + textureStem + "_00";
                pending["dx11/" + UniformPortraitIcon.DirectoryPath + "/" + stem + "_l.g4tx"] = image.Icon.Write(stem, image.Cloth, image.Mask);
            }
        }
        shared = new[] { assetStem, textureStem }; bindings.Add(bindingIdentity, shared);
        return shared;
    }
}
