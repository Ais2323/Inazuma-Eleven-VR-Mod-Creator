using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using G4Composition;
using G4Runtime;
using UnityEngine;
using SFB;

public partial class CharacterManager : MonoBehaviour
{
    public void OnClickSave()
    {
        string defaultName = string.IsNullOrWhiteSpace(nowCharacterData.id)
            ? "character-maker" : nowCharacterData.id;
        string path = StandaloneFileBrowser.SaveFilePanel(
            "儲存 CharacterMaker 狀態", "", defaultName, "charamaker");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!path.EndsWith(".charamaker", StringComparison.OrdinalIgnoreCase))
            path += ".charamaker";

        try
        {
            var document = new CharacterMakerDocument
            {
                version = 14,
                targetId = nowCharacterData.id,
                identity = nowCharacterData.identity?.Clone(),
                voiceCharacterId = nowCharacterData.voiceCharacterId, voiceAvatarCode = nowCharacterData.voiceAvatarCode,
                voiceName = nowCharacterData.voiceName, voicePortraitPng = nowCharacterData.voicePortraitPng,
                avatarCode = avatarSetting != null ? avatarSetting.nowAvatarCode : null,
                avatarInput = avatarSetting != null && avatarSetting.inputField != null ? avatarSetting.inputField.text : null,
                head = MakeSavedPart(1, nowCharacterData.headId),
                hair = MakeSavedPart(2, nowCharacterData.hairId),
                other = MakeSavedPart(3, nowCharacterData.otherId),
                bodyType = nowCharacterData.bodyType,
                bodyVariantId = nowCharacterData.bodyVariantId,
                bodyAvatarCode = nowCharacterData.bodyAvatarCode,
                chestSize = nowCharacterData.chestSize,
                uniformCollar = nowCharacterData.bodyDetailFlags & 0xffff,
                equipmentFlag2 = ((nowCharacterData.bodyDetailFlags >> 16) & 0xffff) == 1 ? 1 : 0,
                uniformHem = (nowCharacterData.bodyDetailFlags >> 16) & 0xffff, uniformSleeve = nowCharacterData.uniformSleeve,
                skinColor = infoPage.GetSelectedSkinColor()
            };
            foreach (var pair in importedTextures.OrderBy(value => value.Key))
            {
                ImportedTexture imported = pair.Value;
                if (imported == null || imported.Dds == null) continue;
                int separator = pair.Key.IndexOf(':');
                if (separator <= 0 || !int.TryParse(pair.Key.Substring(0, separator), out int option)) continue;
                document.textures.Add(new CharacterMakerTexture
                {
                    option = option,
                    sourceId = imported.SourceId,
                    role = imported.Role,
                    avatarAtlasVersion = imported.AvatarAtlasVersion,
                    ddsBase64 = Convert.ToBase64String(imported.Dds)
                });
            }
            try
            {
                if (iconController != null) iconController.SaveUploadedPortrait(document);
                document.portraitG4txBase64 = Convert.ToBase64String(BuildCharacterPortrait(nowCharacterData.id));
            }
            catch (Exception portraitException)
            {
                Debug.LogWarning("頭像未寫入 CharacterMaker 存檔：" + portraitException.Message);
            }
            document.Write(path);
            Debug.Log("CharacterMaker 狀態已儲存：" + path);
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    public void OnClickLoad()
    {
        string[] paths = StandaloneFileBrowser.OpenFilePanel(
            "讀取 CharacterMaker 狀態", "", "charamaker", false);
        if (paths == null || paths.Length == 0 || string.IsNullOrWhiteSpace(paths[0])) return;

        try
        {
            CharacterMakerDocument document = CharacterMakerDocument.Read(paths[0]);

            var warnings = new List<string>();
            RestoreSavedModel(0, document.targetId, warnings);
            if (avatarSetting != null)
            {
                try { avatarSetting.RestoreState(document.avatarCode, document.avatarInput); }
                catch (Exception exception) { warnings.Add("Avatar 設定：" + exception.Message); }
            }
            RestoreSavedModel(1, document.head?.sourceId, warnings, document.head?.avatarCode);
            RestoreSavedModel(2, document.hair?.sourceId, warnings, document.hair?.avatarCode);
            RestoreSavedModel(3, document.other?.sourceId, warnings, document.other?.avatarCode);
            RefreshPageAvailability();

            RestoreSavedTransform(1, document.head, warnings);
            RestoreSavedTransform(2, document.hair, warnings);
            RestoreSavedTransform(3, document.other, warnings);
            infoPage.RestoreIdentity(document.identity);
            infoPage.RestoreSavedBody(document, warnings);
            RestoreSavedTextures(document.textures, warnings);
            try { voiceController.RestoreVoice(document); } catch (Exception exception) { warnings.Add("語音設定：" + exception.Message); }
            try { if (iconController != null) iconController.RestoreUploadedPortrait(document); } catch (Exception exception) { warnings.Add("Icon: " + exception.Message); }
            selectionController.ResetSelectionTarget();

            RefreshAllTransformInputs();
            infoPage.RefreshBodySettingButtons();
            for (int option = 1; option <= 3; option++) RefreshTextureImages(option);
            Debug.Log("CharacterMaker 狀態已讀取：" + paths[0]);
            if (warnings.Count > 0)
                Debug.LogWarning("部分內容未能恢復：\n- " + string.Join("\n- ", warnings));
        }
        catch (Exception exception)
        {
            Debug.LogException(exception);
        }
    }

    private CharacterMakerPart MakeSavedPart(int option, string sourceId)
    {
        GetExtraTransform(option, out Vector3 position, out Vector3 rotation, out Vector3 scale);
        TryGetOptionSelection(option, out _, out Transform[] selected);
        Vector3 exportPosition = option == 1
            ? CalculateExportPosition(headPositionOffset, nowCharacterData.headTransformStartPosition, position, rotation, scale)
            : option == 2
                ? CalculateExportPosition(hairPositionOffset, nowCharacterData.hairTransformStartPosition, position, rotation, scale)
                : CalculateExportPosition(otherPositionOffset, nowCharacterData.otherTransformStartPosition, position, rotation, scale);
        return new CharacterMakerPart
        {
            sourceId = sourceId,
            avatarCode = GetAvatarSource(option),
            position = position,
            rotation = rotation,
            scale = scale,
            exportPosition = exportPosition,
            meshNames = (selected ?? Array.Empty<Transform>()).Where(value => value != null)
                .Select(value => value.name).ToList()
        };
    }

    internal static bool IsSafeCharacterId(string id)
    {
        return !string.IsNullOrWhiteSpace(id) && Regex.IsMatch(id, @"^[A-Za-z0-9_]+$");
    }

    private bool RestoreSavedModel(int option, string sourceId, List<string> warnings, string avatarCode = null)
    {
        if (string.IsNullOrWhiteSpace(sourceId))
        {
            if (option >= 1 && option <= 3)
            {
                ClearImportedTextures(option, false);
                TryGetOptionSelection(option, out _, out Transform[] selected);
                foreach (var part in selected ?? Array.Empty<Transform>()) if (part != null) DestroyRuntimeObject(part.gameObject);
                Transform slot = showSelectModel.GetChild(option);
                for (int i = slot.childCount - 1; i >= 0; i--) DestroyRuntimeObject(slot.GetChild(i).gameObject);
                if (option == 1) { nowCharacterData.head = Array.Empty<Transform>(); nowCharacterData.headId = null; }
                else if (option == 2) { nowCharacterData.hair = Array.Empty<Transform>(); nowCharacterData.hairId = null; }
                else { nowCharacterData.other = Array.Empty<Transform>(); nowCharacterData.otherId = null; }
                SetAvatarSource(option, null);
            }
            return false;
        }
        if (!IsSafeCharacterId(sourceId))
        {
            warnings.Add(OptionName(option) + " 的角色 ID 無效：" + sourceId);
            return false;
        }

        GameObject loaded = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(avatarCode))
            {
                if (option == 0) throw new InvalidDataException("替換角色不能使用 Avatar。");
                var recipe = AvatarRecipeParser.Parse(avatarCode);
                if (sourceId != AvatarCompositionAsset.SourceId(recipe.Code)) throw new InvalidDataException("Avatar 編號與來源 ID 不符。");
                loaded = AvatarHeadBuilder.Build(recipe);
            }
            else
            {
                string relative = FindFaceModel(sourceId).Replace((char)92, '/');
                string relativeFolder = Path.GetDirectoryName(relative).Replace((char)92, '/');
                string modelName = Path.GetFileNameWithoutExtension(relative);
                loaded = G4ModelLoader.CreateModel(Path.Combine("chr", "_face", relativeFolder), modelName);
                CenterSelectionPreview(loaded.transform);
            }
            loaded.name = sourceId;
            int nonFaceCount = loaded.transform.Cast<Transform>().Count(child =>
                !child.name.StartsWith("eye_", StringComparison.OrdinalIgnoreCase) &&
                !child.name.StartsWith("mouth_", StringComparison.OrdinalIgnoreCase));
            if (option == 2 && nonFaceCount < 1)
                throw new InvalidDataException(sourceId + " 沒有 Hair 模型。");
            if (option == 3 && nonFaceCount < 2)
                throw new InvalidDataException(sourceId + " 沒有第二個非臉部模型。");

            bool hasFaceAnchor = TryGetFaceAnchor(loaded.transform, out Vector3 faceAnchor);
            if (option >= 1) ClearImportedTextures(option, false);
            Transform target = showSelectModel.GetChild(option);
            for (int i = target.childCount - 1; i >= 0; i--)
                DestroyRuntimeObject(target.GetChild(i).gameObject);
            loaded.transform.SetParent(target, false);
            nowCharacterData.SetData(option, loaded.transform);
            SetAvatarSource(option, avatarCode);

            if (option == 0)
            {
                hasTargetFaceAnchor = hasFaceAnchor;
                originalTargetFaceAnchor = targetFaceAnchor = faceAnchor;

                ApplyHeadPosition(); ApplyHairPosition(); ApplyOtherPosition();
                infoPage.LoadCharacterBody(sourceId);
                infoPage.LoadIdentityForCharacter(sourceId);
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
            else
            {
                hasOtherSourceFaceAnchor = hasFaceAnchor;
                otherSourceFaceAnchor = faceAnchor;
                RememberBaseTransforms(nowCharacterData.other, otherBasePositions, otherBaseRotations, otherBaseScales);
                ApplyOtherPosition();
            }
            loaded = null;
            return true;
        }
        catch (Exception exception)
        {
            if (loaded != null) DestroyRuntimeObject(loaded);
            warnings.Add(OptionName(option) + "（" + sourceId + "）：" + exception.Message);
            return false;
        }
    }

    internal static void CenterSelectionPreview(Transform model)
    {
        if (model == null) return;
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return;
        // Keep the same centering rule used by ShowFaceItems so SelectCamera
        // renders a model restored directly from an ID in the same place.
        model.position -= renderers[renderers.Length - 1].bounds.center;
    }

    private void RestoreSavedTransform(int option, CharacterMakerPart part, List<string> warnings)
    {
        if (part == null || string.IsNullOrWhiteSpace(part.sourceId)) return;
        TryGetOptionSelection(option, out string currentId, out Transform[] selected);
        if (!string.Equals(currentId, part.sourceId, StringComparison.OrdinalIgnoreCase) ||
            selected == null || selected.Length == 0) return;
        if (!IsFinite(part.position) || !IsFinite(part.rotation) || !IsPositiveFinite(part.scale))
        {
            warnings.Add(OptionName(option) + " 的 Transform 無效，已保留目前值。");
            return;
        }
        SetExtraTransform(option, part.position, part.rotation, part.scale);
        ApplyExtraTransform(option);
    }

    private static bool IsFinite(Vector3 value)
    {
        return float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);
    }

    private static bool IsPositiveFinite(Vector3 value)
    {
        return IsFinite(value) && value.x > 0f && value.y > 0f && value.z > 0f;
    }

    private void RestoreSavedTextures(List<CharacterMakerTexture> textures, List<string> warnings)
    {
        if (textures == null) return;
        foreach (CharacterMakerTexture saved in textures)
        {
            Texture2D preview = null;
            try
            {
                if (saved == null || saved.option < 1 || saved.option > 3 ||
                    string.IsNullOrWhiteSpace(saved.role) || TextureChannel(saved.role) == null)
                    throw new InvalidDataException("貼圖記錄格式無效。");
                TryGetOptionSelection(saved.option, out string sourceId, out Transform[] selected);
                if (!string.Equals(sourceId, saved.sourceId, StringComparison.OrdinalIgnoreCase) ||
                    selected == null || selected.Length == 0)
                    throw new InvalidDataException("來源模型未恢復，因此略過貼圖。");
                byte[] dds = Convert.FromBase64String(saved.ddsBase64 ?? "");
                G4TextureParser.TextureData importedInfo = G4TextureParser.ParseDDS(dds, saved.role);
                RawTexture original = FindCurrentTexture(saved.option, sourceId, selected, saved.role);
                G4TextureParser.TextureData originalInfo = G4TextureParser.ParseDDS(original.Dds, original.Name);
                ValidateImportedDimensions(originalInfo, importedInfo);
                bool linear = saved.role.EndsWith("oc", StringComparison.OrdinalIgnoreCase) ||
                    saved.role.EndsWith("spm", StringComparison.OrdinalIgnoreCase);
                if (saved.option == 2 && !string.IsNullOrWhiteSpace(GetAvatarSource(2)) && saved.avatarAtlasVersion < 1)
                {
                    var avatar = AvatarCompositionAsset.Build(GetAvatarSource(2));
                    dds = RepackLegacyAvatarHairDds(dds, avatar.LegacyHairAtlasBounds, linear);
                }
                string textureName = sourceId + "_" + saved.role.ToLowerInvariant();
                preview = CreatePreviewTexture(dds, textureName, linear);
                ClearImportedTexture(saved.option, saved.role, false);
                importedTextures[ImportKey(saved.option, saved.role)] = new ImportedTexture
                {
                    SourceId = sourceId,
                    Role = saved.role.ToLowerInvariant(),
                    Dds = dds,
                    Preview = preview,
                    AvatarAtlasVersion = 1
                };
                preview = null;
                EnsurePreviewMaterials(saved.option, selected);
                ApplyImportedPreviews(saved.option);
            }
            catch (Exception exception)
            {
                if (preview != null) DestroyRuntimeObject(preview);
                warnings.Add("貼圖 " + (saved?.role ?? "未知") + "：" + exception.Message);
            }
        }
    }

}
