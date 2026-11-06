using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;

public static partial class UniformMakerExporter
{
    static string WritePortraitFiles(UniformMakerVariant variant, CfgTable.Row body, uint sourceModel, uint targetModel,
        string key, int sleeve, Dictionary<string, byte[]> converted, Dictionary<string, byte[]> pending)
    {
        string assetStem = "u" + Digest(key + "|portrait");
        var source = NativeUniformFashion.Resolve(sourceModel, body, sleeve, 0) ?? NativeUniformFashion.Resolve(sourceModel, body, 0, 0);
        var fallback = NativeUniformFashion.Resolve(targetModel, body, sleeve, 0) ?? NativeUniformFashion.Resolve(targetModel, body, 0, 0);
        string textureMember = Path.GetFileNameWithoutExtension((source ?? fallback).UniformTexture).Split('_').Last();
        string textureStem = assetStem + "_" + textureMember;
        for (int collar = 0; collar < 2; collar++) {
            string nativeStem = UniformPortraitIcon.Find(source, body, collar, sleeve) ?? UniformPortraitIcon.Find(fallback, body, collar, sleeve);
            if (nativeStem == null) throw new FileNotFoundException("Missing uniform portrait for body " + body.Int(4));
            var icon = UniformPortraitIcon.Load(nativeStem);
            string png = variant.portraitIcons.FirstOrDefault(p => p.collar == collar && p.sleeve == sleeve)?.pngBase64;
            byte[] cloth = icon.Cloth.Dds;
            if (!string.IsNullOrEmpty(png)) {
                string identity = "portrait|" + icon.Width + "|" + icon.Height + "|" + PayloadHash(png);
                if (!converted.TryGetValue(identity, out cloth)) converted.Add(identity, cloth = icon.EncodeCloth(png));
            }
            for (int style = 0; style < 2; style++) {
                string stem = textureStem + "_" + style + collar;
                pending["dx11/" + UniformPortraitIcon.DirectoryPath + "/" + stem + "_l.g4tx"] = icon.Write(stem, cloth);
            }
            if (collar == 0) {
                string stem = assetStem + "_" + textureStem + "_00";
                pending["dx11/" + UniformPortraitIcon.DirectoryPath + "/" + stem + "_l.g4tx"] = icon.Write(stem, cloth);
            }
        }
        return textureStem;
    }
}
