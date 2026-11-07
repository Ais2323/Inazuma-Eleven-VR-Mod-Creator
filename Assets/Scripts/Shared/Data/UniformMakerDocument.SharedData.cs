using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEngine;

public sealed partial class UniformMakerDocument
{
    sealed class DigestValue { internal string Value; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, DigestValue> contentDigests = new();
    public List<UniformMakerBlob> sharedData = new();
    public List<UniformMakerFbxEntry> sharedFbx = new();

    void ResolveSharedData()
    {
        var payloads = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var blob in sharedData ?? new List<UniformMakerBlob>()) {
            if (blob == null || string.IsNullOrEmpty(blob.id) || string.IsNullOrEmpty(blob.bytesBase64) || payloads.ContainsKey(blob.id))
                throw new InvalidDataException("Invalid shared uniform payload.");
            payloads.Add(blob.id, blob.bytesBase64);
        }
        string Resolve(string id) => payloads.TryGetValue(id, out var bytes) ? bytes : throw new InvalidDataException("Missing shared uniform payload: " + id);
        var models = new Dictionary<string, UniformFbxModel>(StringComparer.Ordinal);
        foreach (var entry in sharedFbx ?? new List<UniformMakerFbxEntry>()) {
            if (entry == null || string.IsNullOrEmpty(entry.id) || entry.model == null || models.ContainsKey(entry.id))
                throw new InvalidDataException("Invalid shared FBX binding.");
            if (!string.IsNullOrEmpty(entry.model.dataId)) entry.model.bytesBase64 = Resolve(entry.model.dataId);
            models.Add(entry.id, entry.model);
        }
        foreach (var variant in variants ?? new List<UniformMakerVariant>()) {
            if (variant == null) continue;
            if (!string.IsNullOrEmpty(variant.fbxId)) {
                if (!models.TryGetValue(variant.fbxId, out var model)) throw new InvalidDataException("Missing shared FBX binding: " + variant.fbxId);
                variant.fbx = model;
            }
            foreach (var texture in variant.textures ?? new List<UniformMakerTexture>())
                if (texture != null && !string.IsNullOrEmpty(texture.dataId)) texture.bytesBase64 = Resolve(texture.dataId);
            foreach (var portrait in variant.portraitIcons ?? new List<UniformMakerPortrait>())
                if (portrait != null) {
                    if (!string.IsNullOrEmpty(portrait.dataId)) portrait.pngBase64 = Resolve(portrait.dataId);
                    if (!string.IsNullOrEmpty(portrait.maskDataId)) portrait.maskPngBase64 = Resolve(portrait.maskDataId);
                }
        }
        sharedData = new List<UniformMakerBlob>(); sharedFbx = new List<UniformMakerFbxEntry>();
    }

    UniformMakerDocument CreateStorageCopy()
    {
        var copy = (UniformMakerDocument)MemberwiseClone();
        copy.version = 2; copy.sharedData = new List<UniformMakerBlob>(); copy.sharedFbx = new List<UniformMakerFbxEntry>();
        copy.variants = new List<UniformMakerVariant>();
        var payloadIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var modelIds = new Dictionary<string, string>(StringComparer.Ordinal);
        string AddPayload(string bytes) {
            if (string.IsNullOrEmpty(bytes)) return null;
            if (payloadIds.TryGetValue(bytes, out var existing)) return existing;
            string id = ContentDigest(bytes); payloadIds.Add(bytes, id);
            copy.sharedData.Add(new UniformMakerBlob { id = id, bytesBase64 = bytes }); return id;
        }
        foreach (var variant in variants) {
            var stored = variant.Copy(); stored.fbx = null; stored.fbxId = null;
            if (variant.fbx != null) {
                var model = variant.fbx.Clone(); model.dataId = AddPayload(model.bytesBase64); model.bytesBase64 = null;
                string identity = JsonUtility.ToJson(model);
                if (!modelIds.TryGetValue(identity, out string id)) {
                    id = ContentDigest(identity); modelIds.Add(identity, id);
                    copy.sharedFbx.Add(new UniformMakerFbxEntry { id = id, model = model });
                }
                stored.fbxId = id;
            }
            stored.textures = variant.textures.Select(texture => {
                var item = texture.Copy(); item.dataId = AddPayload(item.bytesBase64); item.bytesBase64 = null; return item;
            }).ToList();
            stored.portraitIcons = (variant.portraitIcons ?? new List<UniformMakerPortrait>()).Select(portrait => {
                var item = portrait.Copy(); item.dataId = AddPayload(item.pngBase64); item.pngBase64 = null; item.maskDataId = AddPayload(item.maskPngBase64); item.maskPngBase64 = null; return item;
            }).ToList();
            copy.variants.Add(stored);
        }
        return copy;
    }

    internal static string ContentDigest(string value)
    {
        return contentDigests.GetValue(value, text => {
            using var sha = SHA256.Create();
            return new DigestValue { Value = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(text))).Replace("-", "").ToLowerInvariant() };
        }).Value;
    }
}

[Serializable]
public sealed class UniformMakerBlob { public string id, bytesBase64; }
[Serializable]
public sealed class UniformMakerFbxEntry { public string id; public UniformFbxModel model; }
