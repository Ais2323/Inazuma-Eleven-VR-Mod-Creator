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
        var explicitKeys = new HashSet<string>(document.EffectiveVariants().Select(v => v.side + "/" + v.role + "/" + v.bodyAttribute));
        foreach (var variant in document.EffectiveVariants()) {
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
                if (!pending.ContainsKey(md) || !pending.ContainsKey(mg) || !pending.ContainsKey(tx)) {
                    used.UnionWith(new[] { md, mg, tx }); continue;
                }
                string paths = mesh + "|" + texture;
                if (!byPaths.TryGetValue(paths, out var shared)) {
                    string modelContent = Path.GetFileName(mesh) + "|" + Signature(md, mg);
                    if (!modelsByContent.TryGetValue(modelContent, out string sharedModel))
                        modelsByContent.Add(modelContent, sharedModel = mesh);
                    // The texture owner filename is also used by retail portrait
                    // lookup. Share geometry independently of that owner alias.
                    string textureContent = Path.GetFileName(texture) + "|" + Signature(tx);
                    if (!texturesByContent.TryGetValue(textureContent, out string sharedTexture))
                        texturesByContent.Add(textureContent, sharedTexture = texture);
                    shared = new[] { sharedModel, sharedTexture };
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
