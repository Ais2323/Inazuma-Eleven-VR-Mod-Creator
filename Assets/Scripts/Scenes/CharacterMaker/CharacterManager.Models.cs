using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;
using UnityEngine;

public partial class CharacterManager : MonoBehaviour
{
    private string GetAvatarSource(int option) => option == 1 ? nowCharacterData.headAvatarCode :
        option == 2 ? nowCharacterData.hairAvatarCode : option == 3 ? nowCharacterData.otherAvatarCode : null;

    private void SetAvatarSource(int option, string code)
    {
        if (string.IsNullOrWhiteSpace(code)) code = null;
        if (option == 1) nowCharacterData.headAvatarCode = code;
        else if (option == 2) nowCharacterData.hairAvatarCode = code;
        else if (option == 3) nowCharacterData.otherAvatarCode = code;
    }

    private static GameObject CloneAvatarSelection(GameObject source)
    {
        GameObject copy = Instantiate(source);
        copy.name = source.name;
        var owner = copy.GetComponent<RuntimeHeadResources>() ?? copy.AddComponent<RuntimeHeadResources>();
        try
        {
            // The setting preview can be rebuilt independently of selected parts.
            // Duplicate its runtime assets so replacing it cannot delete this selection.
            foreach (var filter in copy.GetComponentsInChildren<MeshFilter>(true))
                if (filter.sharedMesh != null) filter.sharedMesh = owner.Own(Instantiate(filter.sharedMesh));
            var materials = new Dictionary<Material, Material>();
            var textures = new Dictionary<Texture, Texture>();
            foreach (var renderer in copy.GetComponentsInChildren<Renderer>(true))
            {
                Material[] slots = renderer.sharedMaterials;
                for (int i = 0; i < slots.Length; i++)
                {
                    Material original = slots[i];
                    if (original == null) continue;
                    if (!materials.TryGetValue(original, out Material material))
                    {
                        material = owner.Own(Instantiate(original));
                        materials.Add(original, material);
                        foreach (string property in material.GetTexturePropertyNames())
                        {
                            Texture originalTexture = original.GetTexture(property);
                            if (!(originalTexture is Texture2D)) continue;
                            if (!textures.TryGetValue(originalTexture, out Texture texture))
                            {
                                texture = owner.Own(Instantiate(originalTexture));
                                textures.Add(originalTexture, texture);
                            }
                            material.SetTexture(property, texture);
                        }
                    }
                    slots[i] = material;
                }
                renderer.sharedMaterials = slots;
            }
            return copy;
        }
        catch { DestroyRuntimeObject(copy); throw; }
    }

    internal void ApplyCharacterSelection(Transform modelFrom, int option)
    {
        if (option >= 1 && option <= 3)
        {
            ClearImportedTextures(option, true);
            ResetExtraTransform(option);
        }

        // 將指定模型複製過去
        Transform targetSelect = showSelectModel.GetChild(option);

        for(int i = 0 ; i < targetSelect.childCount; i++)
            Destroy(targetSelect.GetChild(i).gameObject);

        bool hasFaceAnchor = TryGetFaceAnchor(modelFrom, out Vector3 faceAnchor);

        modelFrom.SetParent(targetSelect, false);

        nowCharacterData.SetData(option, modelFrom);

        if (option == 0)
        {
            nowCharacterData.templateId = modelFrom.name;
            infoPage.RefreshNewControls();
            if (iconController != null) iconController.ClearUploadedPortrait();
            hasTargetFaceAnchor = hasFaceAnchor;
            originalTargetFaceAnchor = targetFaceAnchor = faceAnchor;

            ApplyHeadPosition();
            ApplyHairPosition();
            ApplyOtherPosition();
            infoPage.LoadCharacterBody(modelFrom.name);
            infoPage.LoadIdentityForCharacter(modelFrom.name);
            if (string.IsNullOrWhiteSpace(nowCharacterData.voiceCharacterId) && string.IsNullOrWhiteSpace(nowCharacterData.voiceAvatarCode))
                voiceController.SetVoiceSelection(modelFrom.name, null, null, null, null);
        }
        else if (option == 1)
        {
            hasHeadSourceFaceAnchor = hasFaceAnchor;
            headSourceFaceAnchor = faceAnchor;
            RememberBaseTransforms(nowCharacterData.head, headBasePositions, headBaseRotations, headBaseScales);
            ApplyHeadPosition();
        }
        else if (option == 2)
        {
            hasHairSourceFaceAnchor = hasFaceAnchor;
            hairSourceFaceAnchor = faceAnchor;
            RememberBaseTransforms(nowCharacterData.hair, hairBasePositions, hairBaseRotations, hairBaseScales);
            ApplyHairPosition();
        }
        else if (option == 3)
        {
            hasOtherSourceFaceAnchor = hasFaceAnchor;
            otherSourceFaceAnchor = faceAnchor;
            RememberBaseTransforms(nowCharacterData.other, otherBasePositions, otherBaseRotations, otherBaseScales);
            ApplyOtherPosition();
            if (nowCharacterData.other == null || nowCharacterData.other.Length == 0)
                Debug.LogWarning(modelFrom.name + " 沒有第二個非臉部模型，Other 已清空。");
        }

        if (option >= 1 && option <= 3)
            RefreshTextureImages(option);

        selectionController.Close();
    }

    private static bool TryGetFaceAnchor(Transform modelRoot, out Vector3 anchor)
    {
        anchor = Vector3.zero;
        if (modelRoot == null) return false;
        var attachment = modelRoot.GetComponent<HeadAttachmentAnchor>();
        if (attachment != null && attachment.TryGetPosition(out anchor)) return true;
        bool found = false;
        Bounds combined = default;
        foreach (MeshFilter filter in modelRoot.GetComponentsInChildren<MeshFilter>(true))
        {
            string partName = filter.transform.name;
            if (!partName.StartsWith("eye_", StringComparison.OrdinalIgnoreCase) &&
                !partName.StartsWith("mouth_", StringComparison.OrdinalIgnoreCase))
                continue;
            Mesh mesh = filter.sharedMesh;
            if (mesh == null) continue;
            Bounds bounds = mesh.bounds;
            Matrix4x4 toRoot = modelRoot.worldToLocalMatrix * filter.transform.localToWorldMatrix;
            Vector3 min = bounds.min;
            Vector3 max = bounds.max;
            for (int x = 0; x < 2; x++)
            for (int y = 0; y < 2; y++)
            for (int z = 0; z < 2; z++)
            {
                Vector3 point = toRoot.MultiplyPoint3x4(new Vector3(
                    x == 0 ? min.x : max.x,
                    y == 0 ? min.y : max.y,
                    z == 0 ? min.z : max.z));
                if (!found)
                {
                    combined = new Bounds(point, Vector3.zero);
                    found = true;
                }
                else combined.Encapsulate(point);
            }
        }
        if (found) anchor = combined.center;
        return found;
    }

    private static void RememberBaseTransforms(
        Transform[] parts,
        Dictionary<Transform, Vector3> positions,
        Dictionary<Transform, Quaternion> rotations,
        Dictionary<Transform, Vector3> scales)
    {
        positions.Clear();
        rotations.Clear();
        scales.Clear();
        if (parts == null) return;
        foreach (Transform part in parts)
            if (part != null)
            {
                positions[part] = part.localPosition;
                rotations[part] = part.localRotation;
                scales[part] = part.localScale;
            }
    }

    internal void ApplyHeadPosition()
    {
        headPositionOffset = hasTargetFaceAnchor && hasHeadSourceFaceAnchor
            ? targetFaceAnchor - headSourceFaceAnchor
            : Vector3.zero;
        nowCharacterData.headTransformStartPosition = GetTransformStartPosition(
            hasTargetFaceAnchor, targetFaceAnchor,
            hasHeadSourceFaceAnchor, headSourceFaceAnchor,
            headPositionOffset);
        ApplyPartTransform(
            nowCharacterData.head, headBasePositions, headBaseRotations, headBaseScales,
            headPositionOffset, nowCharacterData.headTransformStartPosition,
            nowCharacterData.headExtraPosition,
            nowCharacterData.headExtraRotation, nowCharacterData.headExtraScale);
        Debug.Log("Head 定位偏移：" + headPositionOffset);
    }

    internal void ApplyHairPosition()
    {
        hairPositionOffset = hasTargetFaceAnchor && hasHairSourceFaceAnchor
            ? targetFaceAnchor - hairSourceFaceAnchor
            : Vector3.zero;
        nowCharacterData.hairTransformStartPosition = GetTransformStartPosition(
            hasTargetFaceAnchor, targetFaceAnchor,
            hasHairSourceFaceAnchor, hairSourceFaceAnchor,
            hairPositionOffset);
        ApplyPartTransform(
            nowCharacterData.hair, hairBasePositions, hairBaseRotations, hairBaseScales,
            hairPositionOffset, nowCharacterData.hairTransformStartPosition,
            nowCharacterData.hairExtraPosition,
            nowCharacterData.hairExtraRotation, nowCharacterData.hairExtraScale);
        Debug.Log("Hair 定位偏移：" + hairPositionOffset);
    }

    internal void ApplyOtherPosition()
    {
        otherPositionOffset = hasTargetFaceAnchor && hasOtherSourceFaceAnchor
            ? targetFaceAnchor - otherSourceFaceAnchor
            : Vector3.zero;
        nowCharacterData.otherTransformStartPosition = GetTransformStartPosition(
            hasTargetFaceAnchor, targetFaceAnchor,
            hasOtherSourceFaceAnchor, otherSourceFaceAnchor,
            otherPositionOffset);
        ApplyPartTransform(
            nowCharacterData.other, otherBasePositions, otherBaseRotations, otherBaseScales,
            otherPositionOffset, nowCharacterData.otherTransformStartPosition,
            nowCharacterData.otherExtraPosition,
            nowCharacterData.otherExtraRotation, nowCharacterData.otherExtraScale);
        Debug.Log("Other 定位偏移：" + otherPositionOffset);
    }

    private static void ApplyPartTransform(
        Transform[] parts,
        Dictionary<Transform, Vector3> basePositions,
        Dictionary<Transform, Quaternion> baseRotations,
        Dictionary<Transform, Vector3> baseScales,
        Vector3 automaticOffset,
        Vector3 transformStartPosition,
        Vector3 extraPosition,
        Vector3 extraRotation,
        Vector3 extraScale)
    {
        if (parts == null) return;
        Quaternion adjustmentRotation = Quaternion.Euler(extraRotation);
        foreach (Transform part in parts)
        {
            if (part == null || !basePositions.TryGetValue(part, out Vector3 basePosition)) continue;
            Vector3 alignedPosition = basePosition + automaticOffset;
            Vector3 pivotOffset = alignedPosition - transformStartPosition;
            part.localPosition = transformStartPosition + extraPosition
                + adjustmentRotation * Vector3.Scale(pivotOffset, extraScale);
            if (baseRotations.TryGetValue(part, out Quaternion baseRotation))
                part.localRotation = adjustmentRotation * baseRotation;
            if (baseScales.TryGetValue(part, out Vector3 baseScale))
                part.localScale = Vector3.Scale(baseScale, extraScale);
        }
    }

    private static Vector3 GetTransformStartPosition(
        bool hasTargetAnchor,
        Vector3 targetAnchor,
        bool hasSourceAnchor,
        Vector3 sourceAnchor,
        Vector3 automaticOffset)
    {
        if (hasTargetAnchor) return targetAnchor;
        if (hasSourceAnchor) return sourceAnchor + automaticOffset;
        return automaticOffset;
    }

    internal void ApplyExtraTransform(int option)
    {
        if (option == 1) ApplyHeadPosition();
        else if (option == 2) ApplyHairPosition();
        else if (option == 3) ApplyOtherPosition();
    }

    private void ResetExtraTransform(int option)
    {
        SetExtraTransform(option, Vector3.zero, Vector3.zero, Vector3.one);
        RefreshTransformInputs(option);
    }

    internal float GetExtraTransformValue(int option, int inputIndex)
    {
        GetExtraTransform(option, out Vector3 position, out Vector3 rotation, out Vector3 scale);
        if (inputIndex < 3) return position[inputIndex];
        if (inputIndex < 6) return rotation[inputIndex - 3];
        return scale[inputIndex - 6];
    }

    internal void GetExtraTransform(int option, out Vector3 position, out Vector3 rotation, out Vector3 scale)
    {
        if (option == 1)
        {
            position = nowCharacterData.headExtraPosition;
            rotation = nowCharacterData.headExtraRotation;
            scale = nowCharacterData.headExtraScale;
            return;
        }
        if (option == 2)
        {
            position = nowCharacterData.hairExtraPosition;
            rotation = nowCharacterData.hairExtraRotation;
            scale = nowCharacterData.hairExtraScale;
            return;
        }
        position = nowCharacterData.otherExtraPosition;
        rotation = nowCharacterData.otherExtraRotation;
        scale = nowCharacterData.otherExtraScale;
    }

    internal void SetExtraTransform(int option, Vector3 position, Vector3 rotation, Vector3 scale)
    {
        if (option == 1)
        {
            nowCharacterData.headExtraPosition = position;
            nowCharacterData.headExtraRotation = rotation;
            nowCharacterData.headExtraScale = scale;
        }
        else if (option == 2)
        {
            nowCharacterData.hairExtraPosition = position;
            nowCharacterData.hairExtraRotation = rotation;
            nowCharacterData.hairExtraScale = scale;
        }
        else if (option == 3)
        {
            nowCharacterData.otherExtraPosition = position;
            nowCharacterData.otherExtraRotation = rotation;
            nowCharacterData.otherExtraScale = scale;
        }
    }

    private string FindFaceModel(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new InvalidDataException("角色 ID 未設定。");
        if ((string.IsNullOrEmpty(DumpDataManager.CommonPath) || string.IsNullOrEmpty(DumpDataManager.Dx11Path)) &&
            !DumpDataManager.ValidateSavedPath())
            throw new InvalidDataException("Dump 路徑無效：" + DumpDataManager.LastError);

        foreach (string series in SeriesNames)
        {
            string relativePath = Path.Combine(series, id, id + ".g4md");
            string fullPath = Path.Combine(DumpDataManager.CommonPath, "chr", "_face", relativePath);
            if (File.Exists(fullPath)) return relativePath;
        }

        throw new FileNotFoundException("找不到角色模型：" + id);
    }

    private static void AddExportParts(
        CompositionDocument document,
        RawHeadAsset source,
        Transform[] selectedParts,
        Vector3 position,
        Vector3 rotation,
        Vector3 scale)
    {
        var selectedNames = new HashSet<string>(
            (selectedParts ?? Array.Empty<Transform>()).Where(value => value != null).Select(value => value.name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var part in source.Preview.Parts)
        {
            if (!selectedNames.Contains(part.Name)) continue;

            document.Parts.Add(new CompositionPart
            {
                SourceRelativePath = source.RelativePath,
                MeshIndex = part.SourceMeshIndex,
                Name = part.Name,
                Position = position,
                Rotation = rotation,
                Scale = scale,
                Triangles = Enumerable.Range(0, part.Indices.Length / 3).ToArray()
            });
        }
    }

    private static Vector3 CalculateExportPosition(
        Vector3 automaticOffset,
        Vector3 transformStartPosition,
        Vector3 extraPosition,
        Vector3 extraRotation,
        Vector3 extraScale)
    {
        Quaternion rotation = Quaternion.Euler(extraRotation);
        return transformStartPosition + extraPosition
            + rotation * Vector3.Scale(automaticOffset - transformStartPosition, extraScale);
    }

    private static void WaitToDestroy(Transform[] targets)
    {
        if (targets == null) return;
        for(int i = 0 ; i < targets.Length; i++)
            if (targets[i] != null) Destroy(targets[i].gameObject);
    }

    internal void UpdateTargetAnchorForBodyType(int bodyType)
    {
        if (!hasTargetFaceAnchor) return;
        Vector3 attachment = originalTargetFaceAnchor;
        if (infoPage.bodyModelTable != null)
        {
            try
            {
                var original = infoPage.selectedOriginalBodyRow ?? infoPage.FindBodyRow(nowCharacterData.originalBodyType);
                var selected = infoPage.FindBodyRow(bodyType);
                if (original != null && selected != null)
                    attachment += HeadAttachmentAnchor.GetBody(selected) - HeadAttachmentAnchor.GetBody(original);
            }
            catch (Exception exception)
            {
                Debug.LogWarning("Cannot resolve body head attachment: " + exception.Message);
            }
        }
        targetFaceAnchor = attachment;
        ApplyHeadPosition();
        ApplyHairPosition();
        ApplyOtherPosition();
    }

    internal void ApplyAvatarSelection(int option)
    {
        string code = AvatarRecipeParser.Parse(avatarSetting.nowAvatarCode).Code;
        Transform copy = CloneAvatarSelection(avatarSetting.targetModel).transform;
        copy.name = AvatarCompositionAsset.SourceId(code);
        ApplyCharacterSelection(copy, option);
        SetAvatarSource(option, code);
    }

    private void RefreshAllTransformInputs()
    {
        for (int option = 1; option <= 3; option++) RefreshTransformInputs(option);
    }
    private void RefreshTransformInputs(int option) => GetPartPage(option)?.RefreshTransformInputs();

}
