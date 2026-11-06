using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Runtime;

[Serializable]
public sealed class UniformFbxModel
{
    sealed class PayloadLength { internal int Value; }
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<string, PayloadLength> payloadLengths = new();
    public string fileName, bytesBase64;
    public string dataId;
    public List<UniformFbxBoneBinding> bones = new();
    public int[] meshSlots;
    public float scale = 1;
    public bool flipV = true;
    // Unity's inline serialization can turn a null optional class into an empty
    // object on a JSON round trip. It is not an imported, unbound FBX.
    public bool IsEmpty => string.IsNullOrEmpty(fileName) && string.IsNullOrEmpty(bytesBase64) &&
        (bones == null || bones.Count == 0) && (meshSlots == null || meshSlots.Length == 0);
    public void Validate(HashSet<string> validatedPayloads = null)
    {
        if (string.IsNullOrEmpty(bytesBase64) || bones == null || bones.Count == 0 || meshSlots == null || meshSlots.Length == 0 ||
            bones.Any(b => b == null || string.IsNullOrEmpty(b.sourceName) || b.targetHash == 0) || meshSlots.Any(s => s < 0) ||
            scale <= 0 || float.IsNaN(scale) || float.IsInfinity(scale)) throw new InvalidDataException("Incomplete FBX skeleton/mesh binding.");
        if ((validatedPayloads == null || validatedPayloads.Add(bytesBase64)) && payloadLengths.GetValue(bytesBase64, value => new PayloadLength { Value = Convert.FromBase64String(value).Length }).Value > 64 * 1024 * 1024)
            throw new InvalidDataException("FBX exceeds 64 MB.");
    }
    public UniformFbxModel Clone() => IsEmpty ? null : new UniformFbxModel {
        fileName = fileName, bytesBase64 = bytesBase64, dataId = dataId, scale = scale, flipV = flipV,
        meshSlots = meshSlots == null ? null : (int[])meshSlots.Clone(),
        bones = bones?.Select(b => b == null ? null : new UniformFbxBoneBinding { sourceName = b.sourceName, targetHash = b.targetHash }).ToList()
    };
}

[Serializable]
public sealed class UniformFbxBoneBinding { public string sourceName; public uint targetHash; }
