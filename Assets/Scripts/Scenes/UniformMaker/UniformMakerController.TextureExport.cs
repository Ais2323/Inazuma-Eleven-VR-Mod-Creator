using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;
using SFB;
using UnityEngine;

public sealed partial class UniformMakerController
{
    void ExportPart(UniformPartKind kind)
    {
        if (CurrentTexture(kind, 0) == null) return;
        var folders = StandaloneFileBrowser.OpenFolderPanel("Export textures and UV", SettingManager.GetExportDirectory("textureExportDirectory"), false);
        if (folders == null || folders.Length == 0 || string.IsNullOrWhiteSpace(folders[0])) return;
        SettingManager.RememberExportDirectory("textureExportDirectory", folders[0]);
        try
        {
            string folder = Path.Combine(folders[0], catalog.Entries[selected].Key + "_" + side + "_" + role + "_" + kind);
            int count = WritePartTextures(kind, folder);
            Debug.Log("[UniformMaker] Exported " + count + " texture(s) and UV: " + folder, this);
        }
        catch (Exception error) { Debug.LogError("[UniformMaker] " + error.Message, this); }
    }

    internal int WritePartTextures(UniformPartKind kind, string folder)
    {
        var outfit = GetSelection(side, role);
        if (outfit == null) throw new InvalidOperationException("Select a uniform first.");
        string texturePath = (kind == UniformPartKind.Number || kind == UniformPartKind.Name) ? ResolveAccessory(kind, outfit)?.NumberTexture :
            kind == UniformPartKind.Skin ? ResolveAccessory(kind, outfit)?.SkinTexture : outfit.UniformTexture;
        if (kind == UniformPartKind.Armband) texturePath = ResolveArmband(outfit)?.ArmbandTexture;
        if (kind == UniformPartKind.Gloves)
            texturePath = (CurrentEdit?.Gloves > 0 ? catalog.ResolveGloves(CurrentEdit.Gloves, CurrentBody) :
                catalog.ResolveGloves(catalog.Entries[selected], side, CurrentBody))?.Texture;
        if (string.IsNullOrWhiteSpace(texturePath)) throw new InvalidOperationException("This part has no texture.");
        string path = Path.Combine(DumpDataManager.Dx11Path, "chr", Path.ChangeExtension(texturePath, ".g4tx"));
        var native = RawTexture.Read(File.ReadAllBytes(path), path);
        var renderers = PartRenderers(kind).ToArray();
        var names = new HashSet<string>(renderers.SelectMany(r => new[] { "_MainTex", "_OcTex", "_MskTex", "_SpmTex" }
            .Where(p => r.sharedMaterial.HasProperty(p)).Select(p => r.sharedMaterial.GetTexture(p)?.name)).Where(n => n != null));
        var textures = native.Where(t => names.Contains(t.Name)).ToArray();
        if (textures.Length == 0) throw new InvalidDataException("No native textures match this part.");
        var variants = CreateDocument().variants.Where(v => v.side == side && v.role == role).ToArray();
        var saved = (variants.FirstOrDefault(v => v.bodyAttribute == CurrentBody.Int(4)) ?? variants.FirstOrDefault(v => v.bodyAttribute < 0))?.textures;
        // Prepare every payload before writing, so conversion errors cannot leave
        // a mixture of old and new textures in the selected output folder.
        var output = new Dictionary<string, byte[]>();
        foreach (var texture in textures)
        {
            var edit = saved?.FirstOrDefault(t => t.part == kind && t.originalName == texture.Name);
            byte[] dds = edit == null ? texture.Dds : UniformTextureCodec.ToDds(edit);
            string name = Path.GetFileName(texture.Name);
            output.Add(name + ".dds", dds);
            var uvParts = new List<G4ModelParser.Part>();
            foreach (var renderer in renderers)
            {
                if (!new[] { "_MainTex", "_OcTex", "_MskTex", "_SpmTex" }.Any(p => renderer.sharedMaterial.HasProperty(p) && renderer.sharedMaterial.GetTexture(p)?.name == texture.Name)) continue;
                var mesh = renderer.GetComponent<MeshFilter>()?.sharedMesh;
                if (mesh == null) continue;
                // Draw all collar alternatives, and the currently selected atlas
                // cell for numbers. UV guides share the exported DDS orientation.
                Vector2 scale = renderer.sharedMaterial.mainTextureScale, offset = renderer.sharedMaterial.mainTextureOffset;
                uvParts.Add(new G4ModelParser.Part { Name = mesh.name, Positions = mesh.vertices, Indices = mesh.triangles,
                    UV = mesh.uv.Select(uv => Vector2.Scale(uv, scale) + offset).ToArray() });
            }
            output.Add(name + "_UV.png", TextureUvGuide.Create(new RawTexture { Name = texture.Name, Dds = dds }, uvParts));
        }
        Directory.CreateDirectory(folder);
        foreach (var file in output) File.WriteAllBytes(Path.Combine(folder, file.Key), file.Value);
        return textures.Length;
    }
}
