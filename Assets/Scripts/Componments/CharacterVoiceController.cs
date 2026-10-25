using System;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;
using UnityEngine;
using UnityEngine.UI;

public partial class CharacterVoiceController : MonoBehaviour
{
    public CharacterManager manager;
    CharacterData nowCharacterData => manager.nowCharacterData;
    AvatarSettingController avatarSetting => manager.avatarSetting;
    public RawImage voicePortrait;
    public RawImage replacementPortrait;
    Transform voiceModelSlot;
    public RawImage avatarVoicePortrait;
    public RawImage avatarPortrait;
    Texture2D selectedVoicePortrait;

    internal void SelectCharacterVoice(string sourceId)
    {
        SetVoiceSelection(sourceId, null, null, null, null);
    }

    internal void SelectAvatarVoice()
    {
        string code = AvatarRecipeParser.Parse(avatarSetting.nowAvatarCode).Code;
        string voice = AvatarRecipeParser.ResolveVoiceName(AvatarRecipeParser.Parse(code));
        Texture2D portrait = CaptureAvatarVoicePortrait();
        string png = portrait != null ? Convert.ToBase64String(portrait.EncodeToPNG()) : null;
        SetVoiceSelection(null, code, voice, portrait, png);
    }

    internal void SetVoiceSelection(string sourceId, string code, string voice, Texture2D portrait, string png)
    {
        if (!string.IsNullOrWhiteSpace(sourceId)) ShowCharacterVoicePortrait(sourceId);
        else ClearVoiceModel();
        StopVoicePreview();
        nowCharacterData.voiceCharacterId = sourceId;
        nowCharacterData.voiceAvatarCode = code;
        nowCharacterData.voiceName = voice;
        nowCharacterData.voicePortraitPng = png;
        bool isAvatar = !string.IsNullOrWhiteSpace(code);
        if (voicePortrait != null)
        {
            voicePortrait.gameObject.SetActive(!isAvatar);
        }
        if (avatarVoicePortrait != null)
        {
            // Avatar thumbnails stretch to the button; the native atlas image has a fixed size.
            avatarVoicePortrait.texture = isAvatar ? portrait : null;
            avatarVoicePortrait.uvRect = new Rect(0, 0, 1, 1);
            avatarVoicePortrait.gameObject.SetActive(isAvatar);
        }
        if (selectedVoicePortrait != null) CharacterManager.DestroyRuntimeObject(selectedVoicePortrait);
        selectedVoicePortrait = portrait;
        RefreshVoicePreviewButton();
        manager.selectionController.Close();
    }

    void ClearVoiceModel()
    {
        if (voiceModelSlot == null) return;
        for (int i = voiceModelSlot.childCount - 1; i >= 0; i--)
        {
            var child = voiceModelSlot.GetChild(i).gameObject;
            child.SetActive(false);
            CharacterManager.DestroyRuntimeObject(child);
        }
    }

    void ShowCharacterVoicePortrait(string id)
    {
        string folder = CharacterManager.SeriesNames.Select(series => Path.Combine("chr", "_face", series, id))
            .FirstOrDefault(path => File.Exists(Path.Combine(DumpDataManager.CommonPath, path, id + ".g4md")));
        if (folder == null) throw new FileNotFoundException("找不到語音人物的頭像模型：" + id);
        if (manager.showSelectModel == null || replacementPortrait == null)
            throw new InvalidOperationException("語音頭像尚未設定角色選擇的預覽來源。");

        GameObject model = G4ModelLoader.CreateModel(folder, id);
        try
        {
            // Use the same centering, parent coordinates and camera atlas as Replace/Head/Hair.
            CharacterManager.CenterSelectionPreview(model.transform);
            if (voiceModelSlot == null)
            {
                voiceModelSlot = new GameObject("VoicePreview").transform;
                voiceModelSlot.SetParent(manager.showSelectModel, false);
                voiceModelSlot.localPosition = new Vector3(4 * 2f, 0, 0);
            }
            ClearVoiceModel();
            model.transform.SetParent(voiceModelSlot, false);
            if (voicePortrait != null)
            {
                voicePortrait.texture = replacementPortrait.texture;
                Rect tile = replacementPortrait.uvRect;
                tile.x += 4 * tile.width;
                voicePortrait.uvRect = tile;
            }
        }
        catch { CharacterManager.DestroyRuntimeObject(model); throw; }
    }

    Texture2D CaptureAvatarVoicePortrait()
    {
        if (avatarPortrait == null || !(avatarPortrait.texture is RenderTexture target))
            throw new InvalidOperationException("尚未設定自訂角色的頭像預覽來源。");
        var camera = Camera.allCameras.FirstOrDefault(c => c.targetTexture == target);
        if (camera != null) camera.Render();
        var previous = RenderTexture.active;
        try
        {
            RenderTexture.active = target;
            // Preserve the RenderTexture color space when displaying the frozen portrait.
            var texture = new Texture2D(target.width, target.height, TextureFormat.RGBA32, false, !target.sRGB);
            texture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            texture.Apply();
            return texture;
        }
        finally { RenderTexture.active = previous; }
    }

    internal void RestoreVoice(CharacterMakerDocument document)
    {
        if (!string.IsNullOrWhiteSpace(document.voiceAvatarCode))
        {
            string voice = AvatarRecipeParser.ResolveVoiceName(AvatarRecipeParser.Parse(document.voiceAvatarCode));
            Texture2D portrait = null;
            if (!string.IsNullOrWhiteSpace(document.voicePortraitPng))
            {
                bool linear = avatarPortrait != null && avatarPortrait.texture is RenderTexture target && !target.sRGB;
                portrait = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
                if (!portrait.LoadImage(Convert.FromBase64String(document.voicePortraitPng)))
                { CharacterManager.DestroyRuntimeObject(portrait); throw new InvalidDataException("Avatar 語音頭像無效。"); }
            }
            SetVoiceSelection(null, document.voiceAvatarCode, voice, portrait, document.voicePortraitPng);
        }
        else
        {
            string id = string.IsNullOrWhiteSpace(document.voiceCharacterId) ? document.targetId : document.voiceCharacterId;
            if (!CharacterManager.IsSafeCharacterId(id)) throw new InvalidDataException("語音來源角色 ID 無效。");
            SetVoiceSelection(id, null, null, null, null);
        }
    }

    void OnDestroy()
    {
        StopVoicePreview(); ClearVoiceModel();
        if (selectedVoicePortrait != null) CharacterManager.DestroyRuntimeObject(selectedVoicePortrait);
    }

}
