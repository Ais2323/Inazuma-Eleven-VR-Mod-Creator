using UnityEngine;
using UnityEngine.UI;

public partial class CharacterVoiceController
{
    public GameObject voicePanel;
    public Button voiceSettingButton, voiceCloseButton;
    public Button[] voiceTypeButtons;
    public GameObject[] voiceTypePanels;
    public Button[] normalVoiceButtons;
    public VoiceCategoryRow[] voiceCategoryRows;
    bool voicePanelWasOpen;

    void Start()
    {
        if (voiceSettingButton != null) voiceSettingButton.onClick.AddListener(OnClickVoiceSetting);
        if (voiceCloseButton != null) voiceCloseButton.onClick.AddListener(OnClickCloseVoiceSetting);
        if (voiceTypeButtons != null)
            for (int i = 0; i < voiceTypeButtons.Length; i++)
            {
                int type = i;
                if (voiceTypeButtons[i] != null) voiceTypeButtons[i].onClick.AddListener(() => OnClickVoiceType(type));
            }
        if (normalVoiceButtons != null)
            foreach (var button in normalVoiceButtons)
            {
                if (button == null) continue;
                string group = button.gameObject.name;
                button.onClick.AddListener(async () => await PreviewVoiceAsync(group));
            }
        if (voiceCategoryRows != null)
            foreach (var row in voiceCategoryRows)
            {
                if (row.preview != null) row.preview.onClick.AddListener(async () => await PreviewVoiceAsync(row.category));
                if (row.selectVoiceButton != null) row.selectVoiceButton.onClick.AddListener(async () => await SelectVoiceFilesAsync(row));
            }
        InitializeVoiceSkillPages(); RefreshVoiceFileLabels();
        if (voicePanel != null) voicePanel.SetActive(false);
    }

    public void OnClickVoiceSetting()
    {
        if (voicePanel == null) return;
        voicePanel.SetActive(true);
        voicePanelWasOpen = true;
        RebuildVoiceSkillRows();
        OnClickVoiceType(0);
    }

    public void OnClickCloseVoiceSetting()
    {
        CleanupEmptyVoiceSkillRows();
        StopVoicePreview();
        voicePanelWasOpen = false;
        if (voicePanel != null) voicePanel.SetActive(false);
    }

    public void OnClickVoiceType(int type)
    {
        if (voiceTypePanels == null || type < 0 || type >= voiceTypePanels.Length) return;
        CleanupEmptyVoiceSkillRows();
        StopVoicePreview();
        for (int i = 0; i < voiceTypePanels.Length; i++)
        {
            if (voiceTypePanels[i] != null) voiceTypePanels[i].SetActive(i == type);
            if (voiceTypeButtons != null && i < voiceTypeButtons.Length && voiceTypeButtons[i] != null)
                voiceTypeButtons[i].interactable = i != type;
        }
    }
}
