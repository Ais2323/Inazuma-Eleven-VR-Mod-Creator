using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Runtime;
using UnityEngine;
using UnityEngine.UI;

public class CharacterSelectionController : MonoBehaviour
{
    public CharacterManager manager;
    public Transform panel;
    const int VoiceSelection = 4;
    int selectionTarget = -1;
    internal int SelectionTarget => selectionTarget >= 0 ? selectionTarget : manager.nowSelectOption;
    int nowSelectOption => manager.nowSelectOption;
    AvatarSettingController avatarSetting => manager.avatarSetting;
    readonly List<Button> seriesButtons = new();
    readonly List<Button> charactorButtons = new();
    bool initialized;

    internal void Initialize()
    {
        if (initialized) return;
        initialized = true;
        for (int i = 0; i < SeriesButtonsParent.childCount; i++)
        {
            int index = i; var button = SeriesButtonsParent.GetChild(i).GetComponent<Button>();
            button.onClick.AddListener(() => OnClickSeries(index)); seriesButtons.Add(button);
        }
        for (int i = 0; i < characterBtnParent.childCount; i++)
        {
            int index = i; var button = characterBtnParent.GetChild(i).GetComponent<Button>();
            button.onClick.AddListener(() => OnClickCharacter(index)); charactorButtons.Add(button);
        }
        if (avatarBtn != null) avatarBtn.onClick.AddListener(OnClickAvatar);
        RefreshAvailability();
    }
    public void OnClickOpenSubPanel() => OnClickOpenSubPanel(manager.nowSelectOption);
    public void OnClickOpenSubPanel(int target)
    {
        if (target < 0 || target > VoiceSelection) return;
        selectionTarget = target;
        if (target != VoiceSelection) manager.nowSelectOption = target;
        RefreshAvailability(); ShowFaceItems(); panel.gameObject.SetActive(true);
    }
    public void OnClickAvatar()
    {
        if (!CanSelectAvatar()) return;
        if (SelectionTarget == VoiceSelection) manager.voiceController.SelectAvatarVoice();
        else manager.ApplyAvatarSelection(SelectionTarget);
    }
    internal void ResetSelectionTarget() => selectionTarget = -1;
    internal void Close() { if (panel != null) panel.gameObject.SetActive(false); }
    internal void RefreshAvailability() { if (avatarBtn != null) avatarBtn.interactable = CanSelectAvatar(); }
    void Update() { if (manager != null) RefreshAvailability(); }
    void OnDestroy() { if (avatarBtn != null) avatarBtn.onClick.RemoveListener(OnClickAvatar); }
    public Transform SeriesButtonsParent;
    public int nowSeriesIdx = 0;

    public int nowPage = 0;

    public int totalCount = 0;

    public Transform showItemParent;

    public Button PPageButton;
    public Button NPageButton;
    public InputField PageInput;

    public Transform characterBtnParent;

    public Button avatarBtn;

    void ShowFaceItems()
    {
        for(int i = 0 ; i < showItemParent.childCount; i++)
            Destroy(showItemParent.GetChild(i).gameObject);
        // 取得系列路徑
        var seriesPath = Path.Combine("chr", "_face", CharacterManager.SeriesNames[nowSeriesIdx]);
        var fillPath = Path.Combine(DumpDataManager.CommonPath, seriesPath);
        var folder = Directory.GetDirectories(fillPath);
        totalCount = folder.Length;
        for (int i = 0; i < 16; i++)
        {
            int fileIdx = nowPage * 16 + i;
            if (fileIdx >= folder.Length)
            {
                charactorButtons[i].interactable = false;
                continue;
            }
            var path = folder[fileIdx];
            var name = Path.GetFileNameWithoutExtension(path);
            var pivot = new GameObject("pivot");
            pivot.transform.SetParent(showItemParent);
            pivot.transform.localPosition = new Vector3(i * 2f, 0, 0);
            try
            {
                var head = G4ModelLoader.CreateModel(Path.Combine(seriesPath, name), name);
                var allRender = head.GetComponentsInChildren<Renderer>();
                var bounds = allRender[allRender.Length - 1].bounds;
                head.transform.position -= bounds.center;
                head.transform.SetParent(pivot.transform, false);
                charactorButtons[i].interactable = true;
            }
            catch (Exception ex)
            {
                charactorButtons[i].interactable = false;
                Debug.LogWarning(ex);
            }
        }
        PPageButton.interactable = nowPage > 0;
        NPageButton.interactable = nowPage < (int)Math.Floor(totalCount / 16f);
        PageInput.text = nowPage.ToString();
    }

    public void OnClickPPage()
    {
        if (nowPage < 1) return;
        nowPage -= 1;
        ShowFaceItems();
    }

    public void OnClickNPage()
    {
        if (nowPage >= (int)Math.Floor(totalCount / 16f)) return;
        nowPage += 1;
        ShowFaceItems();
    }

    public void OnChangePageValue()
    {
        string input = PageInput.text;
        if (int.TryParse(input, out int page))
        {
            if (page < 0) page = 0;
            if (page >= Math.Ceiling(totalCount / 16f)) page = (int)Math.Ceiling(totalCount / 16f) - 1;
            nowPage = page;
            ShowFaceItems();
        }
    }


    void OnClickSeries(int index)
    {
        nowSeriesIdx = index;
        nowPage = 0;
        ShowFaceItems();
    }
    void OnClickCharacter(int index)
    {
        if (SelectionTarget == VoiceSelection)
        {
            manager.voiceController.SelectCharacterVoice(showItemParent.GetChild(index).GetChild(0).name);
            return;
        }
        manager.ApplyCharacterSelection(showItemParent.GetChild(index).GetChild(0), SelectionTarget);
    }
    private bool CanSelectAvatar()
    {
        if (SelectionTarget == VoiceSelection) return manager.HasAvatarInfo();
        // Option 0 replaces a native character and must never use an Avatar.
        if (nowSelectOption < 1 || nowSelectOption > 3 || avatarSetting == null || avatarSetting.targetModel == null)
            return false;
        Transform root = avatarSetting.targetModel.transform;
        int nonFaceIndex = 0;
        foreach (Transform part in root)
        {
            bool face = part.name.StartsWith("eye_", StringComparison.OrdinalIgnoreCase) ||
                part.name.StartsWith("mouth_", StringComparison.OrdinalIgnoreCase);
            bool hasMesh = part.GetComponentsInChildren<MeshFilter>(true).Any(filter => filter.sharedMesh != null);
            if (face)
            {
                if (nowSelectOption == 1 && hasMesh) return true;
                continue;
            }
            // Match CharacterData: first non-face child is Hair, second is Other.
            if (nowSelectOption == nonFaceIndex + 2) return hasMesh;
            nonFaceIndex++;
        }
        return false;
    }

}
