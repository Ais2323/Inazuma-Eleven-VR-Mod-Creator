using System;
using UnityEngine;
using UnityEngine.UI;
using G4Composition;

public partial class CharacterInfoController
{
    public Button newCharacterButton;
    public Toggle freeSoulToggle;
    RawImage ReplacePortrait => manager != null && manager.voiceController != null ? manager.voiceController.replacementPortrait : null;
    void InitializeNewControls()
    {
        if (newCharacterButton == null || freeSoulToggle == null || ReplacePortrait == null) {
            Debug.LogError("New character controls are not bound in the scene."); return;
        }
        freeSoulToggle.onValueChanged.AddListener(value => nowCharacterData.freeSoulInShop = value);
        RefreshNewControls();
    }
    internal void InitializeNewCharacter()
    {
        // A native template supplies structural defaults only; it is not a replacement target.
        var model = G4ModelLoader.CreateModel(System.IO.Path.Combine("chr", "_face", "01_IE1", "c01000010"), "c01000010");
        manager.ApplyCharacterSelection(model.transform, 0);
        foreach (var text in nowCharacterData.identity.locales) {
            text.fullName = text.shortName = text.shirtName = text.description = "";
            text.japaneseName = text.locale == "ja" ? "" : null;
        }
        OnClickNew(); RefreshIdentityFields();
    }
    public void OnClickNew()
    {
        if (!string.IsNullOrWhiteSpace(nowCharacterData.id)) {
            nowCharacterData.templateId = nowCharacterData.id;
            nowCharacterData.newCharacterKey = Guid.NewGuid().ToString("N");
        }
        nowCharacterData.id = null;
        if (string.IsNullOrWhiteSpace(nowCharacterData.newCharacterKey)) nowCharacterData.newCharacterKey = Guid.NewGuid().ToString("N");
        // Preserve all configured parts, transforms, identity, voice, body and uploaded icons.
        var target = manager.showSelectModel.GetChild(0);
        foreach (Transform child in target) CharacterManager.DestroyRuntimeObject(child.gameObject);
        manager.selectionController.Close();
        RefreshNewControls(); RefreshIdentityFields();
    }
    internal void RefreshNewControls()
    {
        bool isNew = string.IsNullOrWhiteSpace(nowCharacterData.id);
        if (newCharacterButton != null) newCharacterButton.interactable = !isNew;
        if (freeSoulToggle != null) freeSoulToggle.SetIsOnWithoutNotify(nowCharacterData.freeSoulInShop);
        var portrait = ReplacePortrait;
        if (portrait != null) {
            // Keep the shared camera atlas assigned: Voice uses the same texture.
            portrait.gameObject.SetActive(true);
            portrait.enabled = !isNew;
            if (isNew) portrait.canvasRenderer.Clear();
        }
    }
}
