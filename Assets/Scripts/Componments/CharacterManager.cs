using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public partial class CharacterManager : MonoBehaviour
{
    public Transform OptionButtonsParent;
    public Transform OptionMainParent;

    public Text MainTitle;
    public int nowSelectOption = -1;

    public Transform showSelectModel;

    private List<Button> optionButtons = null;

    [SerializeField]
    public CharacterData nowCharacterData = new(WaitToDestroy, Instantiate);

    private readonly Dictionary<Transform, Vector3> headBasePositions = new();
    private readonly Dictionary<Transform, Vector3> hairBasePositions = new();
    private readonly Dictionary<Transform, Vector3> otherBasePositions = new();
    private readonly Dictionary<Transform, Quaternion> headBaseRotations = new();
    private readonly Dictionary<Transform, Quaternion> hairBaseRotations = new();
    private readonly Dictionary<Transform, Quaternion> otherBaseRotations = new();
    private readonly Dictionary<Transform, Vector3> headBaseScales = new();
    private readonly Dictionary<Transform, Vector3> hairBaseScales = new();
    private readonly Dictionary<Transform, Vector3> otherBaseScales = new();
    private Vector3 targetFaceAnchor;
    private Vector3 originalTargetFaceAnchor;
    private Vector3 headSourceFaceAnchor;
    private Vector3 hairSourceFaceAnchor;
    private Vector3 otherSourceFaceAnchor;
    private bool hasTargetFaceAnchor;
    private bool hasHeadSourceFaceAnchor;
    private bool hasHairSourceFaceAnchor;
    private bool hasOtherSourceFaceAnchor;
    private Vector3 headPositionOffset;
    private Vector3 hairPositionOffset;
    private Vector3 otherPositionOffset;

    public Camera charIconCamera;
    public float portraitCameraHeightOffset = .1f;

    public AvatarSettingController avatarSetting;

    internal static readonly string[] SeriesNames = new string[]
    {
      "01_IE1",
      "02_IE2",
      "03_IE3",
      "04_GO1",
      "05_GO2",
      "06_GO3",
      "07_ARES",
      "08_ORION",
      "11_VICTORY"
    };

    internal static void DestroyRuntimeObject(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }

    public CharacterInfoController infoPage;
    public HeadSettingController headPage;
    public HairSettingController hairPage;
    public OtherSettingController otherPage;
    public CharacterSelectionController selectionController;
    public CharacterVoiceController voiceController;
    public CharacterIconController iconController;

    void Awake()
    {
        infoPage.manager = this; selectionController.manager = this; voiceController.manager = this;
        headPage.Initialize(this); hairPage.Initialize(this); otherPage.Initialize(this);
        selectionController.Initialize(); RefreshPageAvailability();
    }
    void OnEnable() { if (selectionController != null && infoPage != null) RefreshPageAvailability(); }
    void Start()
    {
        optionButtons = new();
        for (int i = 0; i < OptionButtonsParent.childCount; i++)
        {
            int option = i; var button = OptionButtonsParent.GetChild(i).GetComponent<Button>();
            button.onClick.AddListener(() => OnClickOption(option)); optionButtons.Add(button);
        }
        bool dumpReady = DumpDataManager.ValidateSavedPath();
        infoPage.Initialize(dumpReady);
        if (iconController != null) iconController.Initialize(dumpReady);
        RefreshAllTransformInputs(); UpdateRightPanel();
    }
    internal void RefreshPageAvailability()
    {
        selectionController.RefreshAvailability(); infoPage.RefreshAvailability(); voiceController.RefreshVoicePreviewButton();
    }
    internal bool HasAvatarInfo() => avatarSetting != null && avatarSetting.targetModel != null &&
        avatarSetting.targetModel.transform.childCount > 0 && !string.IsNullOrWhiteSpace(avatarSetting.nowAvatarCode);
    void OnClickOption(int index)
    {
        selectionController.ResetSelectionTarget(); nowSelectOption = index;
        selectionController.nowSeriesIdx = 0;
        MainTitle.text = optionButtons[index].transform.GetChild(0).GetComponent<Text>().text;
        UpdateRightPanel();
    }
    void UpdateRightPanel()
    {
        RefreshPageAvailability();
        for (int i = 0; i < OptionMainParent.childCount; i++)
            OptionMainParent.GetChild(i).gameObject.SetActive(nowSelectOption == i);
        selectionController.Close();
    }
    internal CharacterPartPageController GetPartPage(int option) => option == 1 ? headPage : option == 2 ? hairPage : option == 3 ? otherPage : null;
    public bool TryGetPreviewNeck(out Vector3 point)
    {
        point = default;
        return infoPage != null && infoPage.TryGetPreviewNeck(out point);
    }
    void OnDestroy()
    {
        ClearImportedTextures(1, false); ClearImportedTextures(2, false); ClearImportedTextures(3, false);
    }

}
