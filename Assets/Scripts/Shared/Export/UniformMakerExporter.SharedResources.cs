using System;
using System.Collections.Generic;
using System.Linq;
using System.IO;
using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using G4Composition;
using UnityEngine;

public static partial class UniformMakerExporter
{
    sealed class ContentHash { internal string Value; }
    static readonly ConditionalWeakTable<string, ContentHash> payloadHashes = new();
    static string PayloadHash(string value) => payloadHashes.GetValue(value, text => new ContentHash { Value = UniformMakerDocument.ContentDigest(text) }).Value;

    static string TextureIdentity(UniformMakerTexture texture) => string.Join("|", texture.part, texture.channel,
        texture.originalName, texture.sharedAcrossBodies, texture.extension, texture.width, texture.height, texture.mipCount, PayloadHash(texture.bytesBase64));

    // Descriptor bytes 4..7 are the payload's position inside its container,
    // rather than texture content or sampling settings.
    static bool SameTexturePayload(RawTexture a, RawTexture b) => a.Dds.SequenceEqual(b.Dds) &&
        a.Descriptor.Take(4).SequenceEqual(b.Descriptor.Take(4)) && a.Descriptor.Skip(8).SequenceEqual(b.Descriptor.Skip(8));

    static string ResourceIdentity(string mesh, string texture, UniformPartKind kind, bool split, UniformMakerVariant variant, UniformCatalog catalog, string ownerName, string modelName)
    {
        string fbx = "";
        if ((kind == UniformPartKind.Upper && (variant.fbx != null || variant.hideUpper || variant.hideLower)) || variant.partFbx.Any(p => p.part == kind || kind == UniformPartKind.Upper && p.part == UniformPartKind.Lower)) {
            var settings = kind == UniformPartKind.Upper ? variant.fbx?.Clone() : null; if (settings != null) { settings.dataId = PayloadHash(settings.bytesBase64); settings.bytesBase64 = null; settings.fileName = null; }
            var body = catalog.BodyByAttribute(variant.bodyAttribute);
            fbx = JsonUtility.ToJson(settings) + "|" + variant.hideUpper + "|" + variant.hideLower + "|" + string.Join(";", variant.partFbx.Where(p => p.part == kind || kind == UniformPartKind.Upper && p.part == UniformPartKind.Lower).Select(p => p.part + ":" + PayloadHash(p.model.bytesBase64) + ":" + JsonUtility.ToJson(new UniformFbxModel { bones = p.model.bones, meshSlots = p.model.meshSlots, scale = p.model.scale, flipV = p.model.flipV }))) + "|" + NativeBodyShape.Skeleton(body).SourceDigest;
        }
        var edits = variant.textures.Where(t => t.part == kind || split && t.part == UniformPartKind.Lower)
            .OrderBy(t => t.part).ThenBy(t => t.channel).ThenBy(t => t.originalName, StringComparer.Ordinal).Select(TextureIdentity);
        return string.Join("|", mesh, texture, kind, split, ownerName, modelName, fbx, string.Join(";", edits));
    }

    static IEnumerable<UniformMakerVariant> ExpandBodyVariants(UniformMakerDocument document, UniformCatalog catalog)
    {
        // Retain legacy font uploads in saves, but they cannot override a single
        // outfit's retail name generator. Do not emit misleading private atlases.
        var variants = document.EffectiveVariants().Select(variant => {
            if (!variant.textures.Any(t => t.part == UniformPartKind.Name)) return variant;
            var copy = variant.Copy(); copy.textures = variant.textures.Where(t => t.part != UniformPartKind.Name).ToList(); return copy;
        }).ToArray();
        var explicitKeys = new HashSet<string>(variants.Select(v => v.side + "/" + v.role + "/" + v.bodyAttribute));
        foreach (var original in variants) {
            var variant = original;
            if (original.bodyAttribute >= 0 && !original.HasModelChanges) {
                var equivalents = NativeBodyTypes.EquivalentAttributes(catalog.BodyByAttribute(original.bodyAttribute));
                var model = variants.FirstOrDefault(v => v.side == original.side && v.role == original.role &&
                    v.HasModelChanges && equivalents.Contains(v.bodyAttribute));
                if (model != null) {
                    // Texture/color sharing creates explicit records for the retail
                    // alternate attributes. Those records must not cancel the model,
                    // accessory visibility or portrait configured for the same shape.
                    variant = model.Copy(); variant.bodyAttribute = original.bodyAttribute;
                    variant.textures = original.textures.Count > 0 ? original.textures : model.textures;
                    variant.hasNameColor = original.hasNameColor; variant.nameColor = original.nameColor;
                    if ((original.portraitIcons?.Count ?? 0) > 0 || original.inheritPortraitCollar || original.inheritPortraitSleeve) {
                        variant.portraitIcons = original.portraitIcons;
                        variant.inheritPortraitCollar = original.inheritPortraitCollar; variant.inheritPortraitSleeve = original.inheritPortraitSleeve;
                    }
                }
            }
            yield return variant;
            if (variant.bodyAttribute < 0) continue;
            foreach (int attribute in NativeBodyTypes.EquivalentAttributes(catalog.BodyByAttribute(variant.bodyAttribute))) {
                if (attribute == variant.bodyAttribute || explicitKeys.Contains(variant.side + "/" + variant.role + "/" + attribute)) continue;
                var alternate = variant.Copy(); alternate.bodyAttribute = attribute; yield return alternate;
            }
        }
    }

    static void CoalesceResources(CfgTable parts, Dictionary<string, byte[]> pending)
    {
        var modelsByContent = new Dictionary<string, string>(StringComparer.Ordinal);
        var texturesByContent = new Dictionary<string, string>(StringComparer.Ordinal);
        var byPaths = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var portraitLayers = new Dictionary<string, List<RawTexture>>(StringComparer.OrdinalIgnoreCase);
        byte[] PortraitBytes(string stem) {
            if (pending.TryGetValue("dx11/" + UniformPortraitIcon.DirectoryPath + "/" + stem + "_l.g4tx", out var bytes)) return bytes;
            string native = UniformPortraitIcon.NativePath(stem);
            return File.Exists(native) ? File.ReadAllBytes(native) : null;
        }
        List<RawTexture> PortraitLayers(string stem, byte[] bytes) {
            if (!portraitLayers.TryGetValue(stem, out var layers)) portraitLayers.Add(stem, layers = RawTexture.Read(bytes, stem).ToList());
            return layers;
        }
        bool SharePortraitTextures(string mesh, string texture, string sharedMesh, string sharedTexture) {
            string oldModel = Path.GetFileNameWithoutExtension(mesh), oldTexture = Path.GetFileNameWithoutExtension(texture);
            string newModel = Path.GetFileNameWithoutExtension(sharedMesh), newTexture = Path.GetFileNameWithoutExtension(sharedTexture);
            if (oldModel.Length != 7 || oldTexture.Length < 7 || newModel.Length != 7 || newTexture.Length < 7) return false;
            var aliases = new List<(string Source, string Target, byte[] Bytes)>();
            foreach (string suffix in new[] { "00", "01", "10", "11", "composite" }) {
                string source = suffix == "composite" ? oldModel + "_" + oldTexture + "_00" : NativeUniformFashion.IconTextureStem(oldModel, oldTexture) + "_" + suffix;
                string target = suffix == "composite" ? newModel + "_" + newTexture + "_00" : NativeUniformFashion.IconTextureStem(newModel, newTexture) + "_" + suffix;
                if (source == target) continue;
                byte[] bytes = PortraitBytes(source);
                // Do not change an implicit retail fallback we cannot verify.
                if (bytes == null) { if (suffix == "composite") continue; return false; }
                byte[] existing = PortraitBytes(target);
                if (existing != null) {
                    var a = PortraitLayers(source, bytes); var b = PortraitLayers(target, existing);
                    if (a.Count != b.Count || a.Any(layer => !b.Any(other => other.Name == target + layer.Name.Substring(source.Length) &&
                        SameTexturePayload(other, layer)))) return false;
                }
                else aliases.Add((source, target, bytes));
            }
            foreach (var alias in aliases) {
                var layers = PortraitLayers(alias.Source, alias.Bytes);
                var cloth = layers.Single(t => t.Name == alias.Source + "_1"); var mask = layers.Single(t => t.Name == alias.Source + "_2");
                var size = G4Runtime.G4TextureParser.ParseDDS(cloth.Dds, cloth.Name);
                var icon = new UniformPortraitIcon { Stem = alias.Source, Container = alias.Bytes, Cloth = cloth, Mask = mask, Width = size.Width, Height = size.Height };
                pending["dx11/" + UniformPortraitIcon.DirectoryPath + "/" + alias.Target + "_l.g4tx"] = icon.Write(alias.Target, cloth.Dds, mask.Dds);
            }
            return true;
        }
        void PreservePortrait(string mesh, string texture, string sharedMesh, string sharedTexture) {
            string oldStem = Path.GetFileNameWithoutExtension(mesh) + "_" + Path.GetFileNameWithoutExtension(texture) + "_00";
            string newStem = Path.GetFileNameWithoutExtension(sharedMesh) + "_" + Path.GetFileNameWithoutExtension(sharedTexture) + "_00";
            if (oldStem == newStem) return;
            string directory = "dx11/" + UniformPortraitIcon.DirectoryPath + "/";
            if (pending.ContainsKey(directory + newStem + "_l.g4tx")) return;
            if (!pending.TryGetValue(directory + oldStem + "_l.g4tx", out var bytes)) {
                string native = UniformPortraitIcon.NativePath(oldStem);
                if (!File.Exists(native)) return;
                bytes = File.ReadAllBytes(native);
            }
            var layers = RawTexture.Read(bytes, oldStem);
            var cloth = layers.Single(t => t.Name == oldStem + "_1");
            var mask = layers.Single(t => t.Name == oldStem + "_2");
            var size = G4Runtime.G4TextureParser.ParseDDS(cloth.Dds, cloth.Name);
            var icon = new UniformPortraitIcon { Stem = oldStem, Container = bytes, Cloth = cloth, Mask = mask, Width = size.Width, Height = size.Height };
            pending.Add(directory + newStem + "_l.g4tx", icon.Write(newStem, cloth.Dds, mask.Dds));
        }
        string Signature(params string[] files) {
            using var hash = SHA256.Create();
            foreach (string file in files) hash.TransformBlock(pending[file], 0, pending[file].Length, null, 0);
            hash.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            return BitConverter.ToString(hash.Hash);
        }
        foreach (var row in parts.Rows) {
            int[] modelFields = row.Name == "CHARA_PARTS_CLOTHES_INFO" ? new[] { 0, 3, 6, 8 } :
                row.Name == "CHARA_PARTS_GLOVE_INFO" ? new[] { 0 } : Array.Empty<int>();
            foreach (int field in modelFields) {
                string mesh = row.Text(field), texture = row.Text(field + 1);
                if (string.IsNullOrEmpty(mesh) || string.IsNullOrEmpty(texture)) continue;
                string md = "common/chr/" + mesh, mg = Path.ChangeExtension(md, ".g4mg"), tx = "dx11/chr/" + texture;
                if (!pending.ContainsKey(md) && !pending.ContainsKey(mg) && !pending.ContainsKey(tx)) continue;
                string paths = mesh + "|" + texture;
                if (!byPaths.TryGetValue(paths, out var shared)) {
                    // Geometry is independent of kit/side/texture owner names. Only
                    // the upper model's body digits participate in portrait lookup.
                    string modelStem = Path.GetFileNameWithoutExtension(mesh);
                    string portraitBody = row.Name == "CHARA_PARTS_CLOTHES_INFO" && field == 0
                        ? (modelStem.Length == 7 ? "body:" + modelStem.Substring(5, 2) : "owner:" + modelStem)
                        : "accessory";
                    string sharedModel = mesh, sharedTexture = texture;
                    if (pending.ContainsKey(md) && pending.ContainsKey(mg)) {
                        string modelContent = portraitBody + "|" + Signature(md, mg);
                        if (!modelsByContent.TryGetValue(modelContent, out sharedModel)) modelsByContent.Add(modelContent, sharedModel = mesh);
                    }
                    if (pending.ContainsKey(tx)) {
                        string textureContent = Signature(tx);
                        if (!texturesByContent.TryGetValue(textureContent, out sharedTexture)) texturesByContent.Add(textureContent, sharedTexture = texture);
                        // Owner names participate in retail portrait lookup. Only
                        // redirect upper textures when every affected icon agrees.
                        if (sharedTexture != texture && row.Name == "CHARA_PARTS_CLOTHES_INFO" && field == 0 &&
                            !SharePortraitTextures(mesh, texture, sharedModel, sharedTexture)) sharedTexture = texture;
                    }
                    shared = new[] { sharedModel, sharedTexture };
                    if (row.Name == "CHARA_PARTS_CLOTHES_INFO" && field == 0)
                        PreservePortrait(mesh, texture, sharedModel, sharedTexture);
                    byPaths.Add(paths, shared);
                }
                Text(row, field, shared[0]); Text(row, field + 1, shared[1]);
                candidates.UnionWith(new[] { md, mg, tx });
                used.Add("common/chr/" + shared[0]); used.Add("common/chr/" + Path.ChangeExtension(shared[0], ".g4mg")); used.Add("dx11/chr/" + shared[1]);
            }
        }
        foreach (string duplicate in candidates.Where(path => !used.Contains(path))) pending.Remove(duplicate);
    }
}
