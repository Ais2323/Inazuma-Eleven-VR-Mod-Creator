using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using G4Composition;
using G4Runtime;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using SFB;

[System.Serializable]
public class CharacterData
{
    public string id;
    public string headId;
    public Transform[] head;
    public string hairId;
    public Transform[] hair;
    public string otherId;
    public Transform[] other;

    public Vector3 headExtraPosition = Vector3.zero;
    public Vector3 headExtraRotation = Vector3.zero;
    public Vector3 headExtraScale = Vector3.one;
    public Vector3 headTransformStartPosition = Vector3.zero;
    public Vector3 hairExtraPosition = Vector3.zero;
    public Vector3 hairExtraRotation = Vector3.zero;
    public Vector3 hairExtraScale = Vector3.one;
    public Vector3 hairTransformStartPosition = Vector3.zero;
    public Vector3 otherExtraPosition = Vector3.zero;
    public Vector3 otherExtraRotation = Vector3.zero;
    public Vector3 otherExtraScale = Vector3.one;
    public Vector3 otherTransformStartPosition = Vector3.zero;

    public Transform nowShowParent;

    private UnityAction<Transform[]> destroyFunc;
    private Func<Transform, Transform, Transform> Instantiate;

    public CharacterData(UnityAction<Transform[]> destroyFunc, Func<Transform, Transform, Transform> newTr)
    {
        this.destroyFunc = destroyFunc;
        Instantiate = newTr;
    }

    public void SetData(int type, Transform tr)
    {
        switch(type)
        {
            case 0:
                SetCharId(tr);
                break;
            case 1:
                SetHead(tr);
                break;
            case 2:
                SetHair(tr);
                break;
            case 3:
                SetOther(tr);
                break;
        }
    }

    private void SetCharId(Transform tr)
    {
        id = tr.name;
    }

    private void SetHead(Transform tr)
    {
        destroyFunc.Invoke(head);
        headId = tr.name;
        List<Transform> find = new();
        for (int i = 0; i < tr.childCount; i++)
        {
            var t = tr.GetChild(i);
            if (t.name.StartsWith("eye_") || t.name.StartsWith("mouth_"))
            {
                var newT = Instantiate.Invoke(t, nowShowParent);
                newT.name = t.name;
                find.Add(newT);
            }
        }

        head = find.ToArray();
    }

    private void SetHair(Transform tr)
    {
        destroyFunc.Invoke(hair);
        hairId = tr.name;
        List<Transform> find = new();
        for (int i = 0; i < tr.childCount; i++)
        {
            var t = tr.GetChild(i);
            if (t.name.StartsWith("eye_") || t.name.StartsWith("mouth_"))
                continue;
            var newT = Instantiate.Invoke(t, nowShowParent);
            newT.name = t.name;
            find.Add(newT);
            break;
        }

        hair = find.ToArray();
    }

    private void SetOther(Transform tr)
    {
        destroyFunc.Invoke(other);
        otherId = null;
        other = Array.Empty<Transform>();
        int nonFaceIndex = 0;
        for (int i = 0; i < tr.childCount; i++)
        {
            Transform part = tr.GetChild(i);
            if (part.name.StartsWith("eye_", StringComparison.OrdinalIgnoreCase) ||
                part.name.StartsWith("mouth_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (nonFaceIndex++ != 1) continue;
            Transform instance = Instantiate.Invoke(part, nowShowParent);
            instance.name = part.name;
            otherId = tr.name;
            other = new[] { instance };
            return;
        }
    }
}

public class CharacterManager : MonoBehaviour
{
    public Transform OptionButtonsParent;
    public Transform OptionMainParent;
    public Transform OptionSubParent;
    public Text MainTitle;
    public int nowSelectOption = -1;

    public Transform SeriesButtonsParent;
    public int nowSeriesIdx = 0;

    public int nowPage = 0;

    public int totalCount = 0;

    public Transform showItemParent;

    public Button PPageButton;
    public Button NPageButton;
    public InputField PageInput;

    public Transform characterBtnParent;

    public Transform showSelectModel;

    private List<Button> optionButtons = null;
    private List<Button> seriesButtons = null;
    private List<Button> charactorButtons = null;

    [SerializeField]
    public CharacterData nowCharacterData = new(WaitToDestroy, Instantiate);

    public List<RawImage> textureImages = new();
    public List<RawImage> ocImages = new();
    public List<RawImage> spmImages = new();

    public List<Transform> transformInputParent = new();

    public List<InputField[]> transformInput = new();

    private sealed class TextureMaterialBinding
    {
        public Renderer Renderer;
        public Material Original;
        public Material Replacement;
    }

    private sealed class ImportedTexture
    {
        public string SourceId;
        public string Role;
        public byte[] Dds;
        public Texture2D Preview;
    }

    private readonly Dictionary<string, ImportedTexture> importedTextures = new();
    private readonly Dictionary<int, List<TextureMaterialBinding>> previewMaterialBindings = new();
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

    private static string[] seriesNames = new string[]
    {
      "20_EDIT",
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

    void Start()
    {
        optionButtons = new();
        for (int i = 0; i < OptionButtonsParent.childCount; i++)
        {
            // 給左側按鈕上事件
            Button btn = OptionButtonsParent.GetChild(i).GetComponent<Button>();
            int tempIdx = i;
            string title = btn.transform.GetChild(0).GetComponent<Text>().text;
            btn.onClick.AddListener(() => OnClickOption(tempIdx, title));
            optionButtons.Add(btn);
        }

        seriesButtons = new();
        for (int i = 0; i < SeriesButtonsParent.childCount; i++)
        {
            // 給左側按鈕上事件
            Button btn = SeriesButtonsParent.GetChild(i).GetComponent<Button>();
            int tempIdx = i;
            btn.onClick.AddListener(() => OnClickSeries(tempIdx));
            seriesButtons.Add(btn);
        }

        charactorButtons = new();
        for (int i = 0; i < characterBtnParent.childCount; i++)
        {
            // 給左側按鈕上事件
            Button btn = characterBtnParent.GetChild(i).GetComponent<Button>();
            int tempIdx = i;
            btn.onClick.AddListener(() => OnClickCharacter(tempIdx));
            charactorButtons.Add(btn);
        }

        // 給每個transformInput綁定
        transformInput = new();
        for(int i = 0; i < transformInputParent.Count; i++)
        {
            var parent = transformInputParent[i];
            if (parent == null)
            {
                transformInput.Add(null);
                continue;
            }
            InputField[] inputs = new InputField[9];
            for (int j = 0; j < 3; j++)
            {
                var type = parent.GetChild(j).GetChild(1);
                for (int k = 0; k < 3; k++)
                {
                    inputs[j * 3 + k] = type.GetChild(k).GetComponent<InputField>();
                }
            }
            transformInput.Add(inputs);
            BindTransformInputs(i, inputs);
        }

        RefreshAllTransformInputs();

        // 隱藏右側欄位
        UpdateRightPanel();

        // 測試用
        Debug.Log(DumpDataManager.ValidateSavedPath());
    }

    void OnClickOption(int index, string title)
    {
        nowSelectOption = index;
        nowSeriesIdx = 0;
        MainTitle.text = title;
        UpdateRightPanel();
    }

    void OnClickSeries(int index)
    {
        nowSeriesIdx = index;
        nowPage = 0;
        ShowFaceItems();
    }

    void OnClickCharacter(int index)
    {
        if (nowSelectOption >= 1 && nowSelectOption <= 3)
        {
            ClearImportedTextures(nowSelectOption, true);
            ResetExtraTransform(nowSelectOption);
        }

        // 將指定模型複製過去
        Transform targetSelect = showSelectModel.GetChild(nowSelectOption);

        for(int i = 0 ; i < targetSelect.childCount; i++)
            Destroy(targetSelect.GetChild(i).gameObject);

        Transform modelFrom = showItemParent.GetChild(index).GetChild(0);
        bool hasFaceAnchor = TryGetFaceAnchor(modelFrom, out Vector3 faceAnchor);

        modelFrom.SetParent(targetSelect, false);

        nowCharacterData.SetData(nowSelectOption, modelFrom);

        if (nowSelectOption == 0)
        {
            hasTargetFaceAnchor = hasFaceAnchor;
            targetFaceAnchor = faceAnchor;
            ApplyHeadPosition();
            ApplyHairPosition();
            ApplyOtherPosition();
        }
        else if (nowSelectOption == 1)
        {
            hasHeadSourceFaceAnchor = hasFaceAnchor;
            headSourceFaceAnchor = faceAnchor;
            RememberBaseTransforms(nowCharacterData.head, headBasePositions, headBaseRotations, headBaseScales);
            ApplyHeadPosition();
        }
        else if (nowSelectOption == 2)
        {
            hasHairSourceFaceAnchor = hasFaceAnchor;
            hairSourceFaceAnchor = faceAnchor;
            RememberBaseTransforms(nowCharacterData.hair, hairBasePositions, hairBaseRotations, hairBaseScales);
            ApplyHairPosition();
        }
        else if (nowSelectOption == 3)
        {
            hasOtherSourceFaceAnchor = hasFaceAnchor;
            otherSourceFaceAnchor = faceAnchor;
            RememberBaseTransforms(nowCharacterData.other, otherBasePositions, otherBaseRotations, otherBaseScales);
            ApplyOtherPosition();
            if (nowCharacterData.other == null || nowCharacterData.other.Length == 0)
                Debug.LogWarning(modelFrom.name + " 沒有第二個非臉部模型，Other 已清空。");
        }

        if (nowSelectOption >= 1 && nowSelectOption <= 3)
            RefreshTextureImages(nowSelectOption);

        OptionSubParent.gameObject.SetActive(false);
    }
    private static bool TryGetFaceAnchor(Transform modelRoot, out Vector3 anchor)
    {
        anchor = Vector3.zero;
        if (modelRoot == null) return false;
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

    private void ApplyHeadPosition()
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

    private void ApplyHairPosition()
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

    private void ApplyOtherPosition()
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

    private void BindTransformInputs(int option, InputField[] inputs)
    {
        if (option < 1 || option > 3) return;
        for (int i = 0; i < inputs.Length; i++)
        {
            int inputIndex = i;
            inputs[i].onValueChanged.AddListener(value => OnTransformInputChanged(option, inputIndex, value));
            inputs[i].onEndEdit.AddListener(_ => NormalizeTransformInput(option, inputIndex));
        }
    }

    private void OnTransformInputChanged(int option, int inputIndex, string value)
    {
        if (!TryParseFiniteFloat(value, out float number)) return;
        if (inputIndex >= 6 && number <= 0f) return;

        GetExtraTransform(option, out Vector3 position, out Vector3 rotation, out Vector3 scale);
        int axis = inputIndex % 3;
        if (inputIndex < 3) position[axis] = number;
        else if (inputIndex < 6) rotation[axis] = number;
        else scale[axis] = number;
        SetExtraTransform(option, position, rotation, scale);
        ApplyExtraTransform(option);
    }

    private void NormalizeTransformInput(int option, int inputIndex)
    {
        if (option < 0 || option >= transformInput.Count || transformInput[option] == null) return;
        InputField input = transformInput[option][inputIndex];
        if (!TryParseFiniteFloat(input.text, out float number) || (inputIndex >= 6 && number <= 0f))
        {
            number = inputIndex >= 6 ? 1f : GetExtraTransformValue(option, inputIndex);
            if (inputIndex >= 6)
            {
                GetExtraTransform(option, out Vector3 position, out Vector3 rotation, out Vector3 scale);
                scale[inputIndex % 3] = 1f;
                SetExtraTransform(option, position, rotation, scale);
                ApplyExtraTransform(option);
            }
        }
        input.SetTextWithoutNotify(FormatTransformValue(number));
    }

    private static bool TryParseFiniteFloat(string value, out float number)
    {
        bool parsed = float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number);
        return parsed && !float.IsNaN(number) && !float.IsInfinity(number);
    }

    private static string FormatTransformValue(float value)
    {
        return value.ToString("0.######", CultureInfo.InvariantCulture);
    }

    private void ApplyExtraTransform(int option)
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

    private void RefreshAllTransformInputs()
    {
        RefreshTransformInputs(1);
        RefreshTransformInputs(2);
        RefreshTransformInputs(3);
    }

    private void RefreshTransformInputs(int option)
    {
        if (option < 0 || option >= transformInput.Count || transformInput[option] == null) return;
        for (int i = 0; i < transformInput[option].Length; i++)
            transformInput[option][i].SetTextWithoutNotify(FormatTransformValue(GetExtraTransformValue(option, i)));
    }

    private float GetExtraTransformValue(int option, int inputIndex)
    {
        GetExtraTransform(option, out Vector3 position, out Vector3 rotation, out Vector3 scale);
        if (inputIndex < 3) return position[inputIndex];
        if (inputIndex < 6) return rotation[inputIndex - 3];
        return scale[inputIndex - 6];
    }

    private void GetExtraTransform(int option, out Vector3 position, out Vector3 rotation, out Vector3 scale)
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

    private void SetExtraTransform(int option, Vector3 position, Vector3 rotation, Vector3 scale)
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
    void UpdateRightPanel()
    {
        for (int i = 0; i < OptionMainParent.childCount; i++)
        {
            var panel = OptionMainParent.GetChild(i);
            panel.gameObject.SetActive(nowSelectOption == i);
        }

        OptionSubParent.gameObject.SetActive(false);
    }

    void ShowFaceItems()
    {
        for(int i = 0 ; i < showItemParent.childCount; i++)
            Destroy(showItemParent.GetChild(i).gameObject);
        // 取得系列路徑
        var seriesPath = Path.Combine("chr", "_face", seriesNames[nowSeriesIdx]);
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

    public void OnClickOpenSubPanel()
    {
        ShowFaceItems();
        OptionSubParent.gameObject.SetActive(true);
    }

    public void OnClickExport()
    {
        string[] folders = StandaloneFileBrowser.OpenFolderPanel("選擇輸出 data 資料夾", "", false);
        if (folders == null || folders.Length == 0 || string.IsNullOrEmpty(folders[0])) return;

        try
        {
            string targetRelativePath = FindFaceModel(nowCharacterData.id);
            string headRelativePath = FindFaceModel(nowCharacterData.headId);
            string hairRelativePath = FindFaceModel(nowCharacterData.hairId);

            RawHeadAsset target = RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path,
                targetRelativePath, CancellationToken.None);
            RawHeadAsset head = RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path,
                headRelativePath, CancellationToken.None);
            RawHeadAsset hair = RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path,
                hairRelativePath, CancellationToken.None);
            RawHeadAsset other = null;
            if (!string.IsNullOrEmpty(nowCharacterData.otherId) &&
                nowCharacterData.other != null && nowCharacterData.other.Length > 0)
            {
                string otherRelativePath = FindFaceModel(nowCharacterData.otherId);
                other = RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path,
                    otherRelativePath, CancellationToken.None);
            }

            var sources = new Dictionary<string, RawHeadAsset>(StringComparer.OrdinalIgnoreCase);
            sources[target.RelativePath] = target;
            sources[head.RelativePath] = head;
            sources[hair.RelativePath] = hair;
            if (other != null) sources[other.RelativePath] = other;

            var document = new CompositionDocument();
            AddExportParts(document, hair, nowCharacterData.hair,
                CalculateExportPosition(
                    hairPositionOffset, nowCharacterData.hairTransformStartPosition,
                    nowCharacterData.hairExtraPosition, nowCharacterData.hairExtraRotation,
                    nowCharacterData.hairExtraScale),
                nowCharacterData.hairExtraRotation, nowCharacterData.hairExtraScale);
            if (other != null)
                AddExportParts(document, other, nowCharacterData.other,
                    CalculateExportPosition(
                        otherPositionOffset, nowCharacterData.otherTransformStartPosition,
                        nowCharacterData.otherExtraPosition, nowCharacterData.otherExtraRotation,
                        nowCharacterData.otherExtraScale),
                    nowCharacterData.otherExtraRotation, nowCharacterData.otherExtraScale);
            AddExportParts(document, head, nowCharacterData.head,
                CalculateExportPosition(
                    headPositionOffset, nowCharacterData.headTransformStartPosition,
                    nowCharacterData.headExtraPosition, nowCharacterData.headExtraRotation,
                    nowCharacterData.headExtraScale),
                nowCharacterData.headExtraRotation, nowCharacterData.headExtraScale);

            G4CompositionWriter.Result result = new G4CompositionWriter().Build(
                document, sources, target, CancellationToken.None);
            var optionSources = new Dictionary<int, RawHeadAsset> { { 1, head }, { 2, hair } };
            if (other != null) optionSources.Add(3, other);
            result.Textures = ApplySelectedTextures(
                result.Metadata, result.Geometry, result.Textures, optionSources);

            byte[] portrait = BuildCharacterPortrait(nowCharacterData.id);

            string relativeFolder = Path.GetDirectoryName(targetRelativePath);
            string commonOutput = Path.Combine(folders[0], "common", "chr", "_face", relativeFolder);
            string dx11Output = Path.Combine(folders[0], "dx11", "chr", "_face", relativeFolder);
            string portraitOutput = Path.Combine(folders[0], "dx11", "menu", "200_icon", "10_icon_chr", "face");
            Directory.CreateDirectory(commonOutput);
            Directory.CreateDirectory(dx11Output);
            Directory.CreateDirectory(portraitOutput);

            File.WriteAllBytes(Path.Combine(commonOutput, nowCharacterData.id + ".g4md"), result.Metadata);
            File.WriteAllBytes(Path.Combine(commonOutput, nowCharacterData.id + ".g4mg"), result.Geometry);
            File.WriteAllBytes(Path.Combine(dx11Output, nowCharacterData.id + ".g4tx"), result.Textures);
            File.WriteAllBytes(Path.Combine(portraitOutput, nowCharacterData.id + "_l.g4tx"), portrait);

            Debug.Log("匯出完成：" + folders[0]);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }

    private byte[] BuildCharacterPortrait(string characterId)
    {
        if (charIconCamera == null)
            throw new InvalidDataException("CharIconCamera 未設定。");
        RenderTexture target = charIconCamera.targetTexture;
        if (target == null)
            throw new InvalidDataException("CharIconCamera 沒有設定 RenderTexture。");
        if (!hasTargetFaceAnchor)
            throw new InvalidDataException("尚未取得替換角色的臉部高度，無法輸出頭像。");

        string templatePath = Path.Combine(
            DumpDataManager.Dx11Path, "menu", "200_icon", "10_icon_chr", "face",
            characterId + "_l.g4tx");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("找不到角色頭像範本：" + templatePath, templatePath);

        byte[] template = File.ReadAllBytes(templatePath);
        List<RawTexture> layers = RawTexture.Read(template, templatePath);
        if (layers.Count != 2)
            throw new InvalidDataException(templatePath + "：頭像 G4TX 必須包含兩張貼圖。");

        int width = ReadPortraitDimension(layers[0].Dds, 16, layers[0].Name, "寬度");
        int height = ReadPortraitDimension(layers[0].Dds, 12, layers[0].Name, "高度");
        if (width != 256 || height != 256 || target.width != width || target.height != height)
            throw new InvalidDataException(
                "頭像範本與 CharIcon RenderTexture 必須都是 256x256。範本：" +
                width + "x" + height + "，RenderTexture：" + target.width + "x" + target.height + "。");

        for (int i = 1; i < layers.Count; i++)
        {
            int layerWidth = ReadPortraitDimension(layers[i].Dds, 16, layers[i].Name, "寬度");
            int layerHeight = ReadPortraitDimension(layers[i].Dds, 12, layers[i].Name, "高度");
            if (layerWidth != width || layerHeight != height)
                throw new InvalidDataException(templatePath + "：兩張頭像貼圖尺寸不一致。");
        }

        Vector3 originalPosition = charIconCamera.transform.position;
        RenderTexture previousActive = RenderTexture.active;
        try
        {
            Vector3 capturePosition = originalPosition;
            capturePosition.y = targetFaceAnchor.y;

            capturePosition.z = -1f;
            charIconCamera.transform.position = capturePosition;
            byte[] front = CapturePortraitDds(charIconCamera, target, width, height);

            capturePosition.z = 0f;
            charIconCamera.transform.position = capturePosition;
            byte[] rear = CapturePortraitDds(charIconCamera, target, width, height);

            byte[] output = ReplacePortraitDdsObject(template, layers, 0, rear, templatePath);
            List<RawTexture> updatedLayers = RawTexture.Read(output, "portrait rear output");
            return ReplacePortraitDdsObject(output, updatedLayers, 1, front, templatePath);
        }
        finally
        {
            charIconCamera.transform.position = originalPosition;
            RenderTexture.active = previousActive;
        }
    }

    private static int ReadPortraitDimension(byte[] dds, int offset, string name, string field)
    {
        if (dds == null || dds.Length < 128 ||
            dds[0] != (byte)'D' || dds[1] != (byte)'D' ||
            dds[2] != (byte)'S' || dds[3] != (byte)' ')
            throw new InvalidDataException(name + " 不是有效的 DDS。");
        int value = BitConverter.ToInt32(dds, offset);
        if (value <= 0) throw new InvalidDataException(name + " 的 DDS " + field + "無效。");
        return value;
    }

    private static byte[] CapturePortraitDds(Camera camera, RenderTexture target, int width, int height)
    {
        Texture2D capture = null;
        try
        {
            if (!target.IsCreated()) target.Create();
            camera.Render();
            RenderTexture.active = target;
            capture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            capture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            capture.Apply(false, false);

            Color32[] unityPixels = capture.GetPixels32();
            if (!target.sRGB)
            {
                for (int i = 0; i < unityPixels.Length; i++)
                {
                    Color32 pixel = unityPixels[i];
                    pixel.r = LinearByteToSrgb(pixel.r);
                    pixel.g = LinearByteToSrgb(pixel.g);
                    pixel.b = LinearByteToSrgb(pixel.b);
                    unityPixels[i] = pixel;
                }
            }
            var ddsPixels = new Color32[unityPixels.Length];
            for (int y = 0; y < height; y++)
                Array.Copy(unityPixels, y * width, ddsPixels, (height - 1 - y) * width, width);
            return Bc7PortraitEncoder.Encode(ddsPixels, width, height, CancellationToken.None);
        }
        finally
        {
            if (capture != null)
            {
                if (Application.isPlaying) Destroy(capture);
                else DestroyImmediate(capture);
            }
        }
    }

    private static byte LinearByteToSrgb(byte value)
    {
        float linear = value / 255f;
        return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(linear) * 255f), 0, 255);
    }

    private static byte[] ReplacePortraitDdsObject(
        byte[] template,
        IReadOnlyList<RawTexture> layers,
        int layerIndex,
        byte[] replacement,
        string path)
    {
        if (layerIndex < 0 || layerIndex >= layers.Count)
            throw new InvalidDataException(path + "：指定的頭像圖層不存在。");

        var reader = new G4ModelParser.Binary(template, path);
        int header = reader.U16(4);
        int count = reader.U16(0x20);
        int payload = checked((header + reader.Int(12) + 15) & ~15);
        if (header != 0x60 || count != layers.Count)
            throw new InvalidDataException(path + "：不支援的頭像 G4TX 結構。");

        int descriptor = header + layerIndex * 48;
        int objectOffset = checked(payload + reader.Int(descriptor + 4));
        int objectSize = reader.Int(descriptor + 8);
        if (replacement.Length != objectSize || layers[layerIndex].Dds.Length != objectSize)
            throw new InvalidDataException(
                layers[layerIndex].Name + "：編碼後 DDS 大小與原生頭像槽位不同（" +
                replacement.Length + " / " + objectSize + " bytes）。");
        reader.Range(objectOffset, objectSize);

        byte[] output = (byte[])template.Clone();
        Buffer.BlockCopy(replacement, 0, output, objectOffset, objectSize);

        List<RawTexture> check = RawTexture.Read(output, "portrait output");
        for (int i = 0; i < layers.Count; i++)
        {
            if (!check[i].Descriptor.SequenceEqual(layers[i].Descriptor) ||
                check[i].Name != layers[i].Name || check[i].Hash != layers[i].Hash)
                throw new InvalidDataException("頭像 G4TX 的原生描述資料在輸出時被改變：" + layers[i].Name);
            byte[] expected = i == layerIndex ? replacement : layers[i].Dds;
            if (!check[i].Dds.SequenceEqual(expected))
                throw new InvalidDataException("頭像 G4TX 回讀驗證失敗：" + layers[i].Name);
        }
        return output;
    }

    private string FindFaceModel(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new InvalidDataException("角色 ID 未設定。");
        if ((string.IsNullOrEmpty(DumpDataManager.CommonPath) || string.IsNullOrEmpty(DumpDataManager.Dx11Path)) &&
            !DumpDataManager.ValidateSavedPath())
            throw new InvalidDataException("Dump 路徑無效：" + DumpDataManager.LastError);

        foreach (string series in seriesNames)
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

    public void OnClickExportTextureAndUV()
    {
        if (!TryGetOptionSelection(nowSelectOption, out string sourceId, out Transform[] selectedTransforms))
        {
            Debug.LogError("請先選擇 Head、Hair 或 Other。");
            return;
        }
        if (string.IsNullOrEmpty(sourceId) || selectedTransforms == null || selectedTransforms.Length == 0)
        {
            Debug.LogError("尚未選擇 " + OptionName(nowSelectOption) + " 模型。");
            return;
        }

        string[] folders = StandaloneFileBrowser.OpenFolderPanel("選擇貼圖與 UV 輸出資料夾", "", false);
        if (folders == null || folders.Length == 0 || string.IsNullOrEmpty(folders[0])) return;

        try
        {
            string relativePath = FindFaceModel(sourceId);
            RawHeadAsset source = RawHeadAsset.Read(
                DumpDataManager.CommonPath,
                DumpDataManager.Dx11Path,
                relativePath,
                CancellationToken.None);
            if (source.TextureContainer == null)
                throw new FileNotFoundException("找不到 G4TX：" + sourceId);

            var selectedNames = new HashSet<string>(
                selectedTransforms.Where(item => item != null).Select(item => item.name),
                StringComparer.OrdinalIgnoreCase);
            var texturesByHash = source.Textures.ToDictionary(texture => texture.Hash);
            var usedTextures = new Dictionary<uint, RawTexture>();
            var baseTextureParts = new Dictionary<uint, List<G4ModelParser.Part>>();
            foreach (var part in source.Preview.Parts)
            {
                if (!selectedNames.Contains(part.Name)) continue;
                if (!HasUv(source, part))
                    throw new InvalidDataException(part.Name + " 沒有 UV 資料。");
                if (part.Material < 0 || part.Material >= source.Preview.Materials.Count)
                    throw new InvalidDataException(part.Name + " 的材質索引無效。");

                foreach (uint hash in source.Preview.Materials[part.Material].TextureHashes.Distinct())
                {
                    if (!texturesByHash.TryGetValue(hash, out var texture))
                        throw new InvalidDataException("G4TX 找不到材質引用的貼圖：" + hash.ToString("X8"));
                    string role = TextureRole(texture.Name);
                    if (TextureChannel(role) == null) continue;
                    usedTextures[hash] = texture;
                    if (TextureChannel(role) == "base")
                    {
                        if (!baseTextureParts.TryGetValue(hash, out var parts))
                            baseTextureParts.Add(hash, parts = new List<G4ModelParser.Part>());
                        if (!parts.Contains(part)) parts.Add(part);
                    }
                }
            }

            if (baseTextureParts.Count == 0)
                throw new InvalidDataException("找不到無後綴主貼圖。");

            foreach (var item in usedTextures)
            {
                RawTexture texture = item.Value;
                string role = TextureRole(texture.Name);
                byte[] dds = texture.Dds;
                if (importedTextures.TryGetValue(ImportKey(nowSelectOption, role), out var imported) &&
                    imported.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
                    dds = imported.Dds;
                File.WriteAllBytes(Path.Combine(folders[0], SafeFileName(texture.Name) + ".dds"), dds);
            }

            foreach (var item in baseTextureParts)
            {
                RawTexture texture = texturesByHash[item.Key];
                byte[] dds = texture.Dds;
                string baseRole = TextureRole(texture.Name);
                if (importedTextures.TryGetValue(ImportKey(nowSelectOption, baseRole), out var imported) &&
                    imported.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
                    dds = imported.Dds;
                var uvTexture = new RawTexture { Name = texture.Name, Dds = dds };
                File.WriteAllBytes(
                    Path.Combine(folders[0], SafeFileName(texture.Name) + "_UV.png"),
                    CreateUvPng(uvTexture, item.Value));
            }

            Debug.Log("貼圖與 UV 已輸出：" + folders[0]);
        }
        catch (Exception ex)
        {
            Debug.LogException(ex);
        }
    }
    private static bool HasUv(RawHeadAsset source, G4ModelParser.Part part)
    {
        RawMesh mesh = source.Meshes[part.SourceMeshIndex];
        byte[] layout = source.Layouts[mesh.Layout];
        for (int i = 0; i < layout[1]; i++)
            if (layout[8 + i * 8] == 10) return true;
        return false;
    }

    private static byte[] CreateUvPng(RawTexture texture, IEnumerable<G4ModelParser.Part> parts)
    {
        if (texture.Dds == null || texture.Dds.Length < 20 ||
            texture.Dds[0] != (byte)'D' || texture.Dds[1] != (byte)'D' ||
            texture.Dds[2] != (byte)'S' || texture.Dds[3] != (byte)' ')
            throw new InvalidDataException(texture.Name + " 不是有效的 DDS。");
        int height = BitConverter.ToInt32(texture.Dds, 12);
        int width = BitConverter.ToInt32(texture.Dds, 16);
        if (width < 1 || height < 1 || width > 16384 || height > 16384)
            throw new InvalidDataException(texture.Name + " 的 DDS 尺寸無效。");

        var pixels = new Color32[checked(width * height)];
        foreach (var part in parts)
        {
            if (part.UV == null || part.UV.Length != part.Positions.Length)
                throw new InvalidDataException(part.Name + " 的 UV 數量無效。");
            if (part.Indices == null || part.Indices.Length % 3 != 0)
                throw new InvalidDataException(part.Name + " 的三角形索引無效。");
            for (int i = 0; i < part.Indices.Length; i += 3)
            {
                Vector2 a = part.UV[part.Indices[i]];
                Vector2 b = part.UV[part.Indices[i + 1]];
                Vector2 c = part.UV[part.Indices[i + 2]];
                DrawRepeatedUvLine(pixels, width, height, a, b);
                DrawRepeatedUvLine(pixels, width, height, b, c);
                DrawRepeatedUvLine(pixels, width, height, c, a);
            }
        }

        var image = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
        try
        {
            image.SetPixels32(pixels);
            image.Apply(false, false);
            return image.EncodeToPNG();
        }
        finally
        {
            if (Application.isPlaying) Destroy(image); else DestroyImmediate(image);
        }
    }

    private static void DrawRepeatedUvLine(Color32[] pixels, int width, int height, Vector2 a, Vector2 b)
    {
        if (!IsFinite(a) || !IsFinite(b)) throw new InvalidDataException("UV 包含非有限數值。");
        int minTileX = Mathf.CeilToInt(Mathf.Min(a.x, b.x) - 1f);
        int maxTileX = Mathf.FloorToInt(Mathf.Max(a.x, b.x));
        int minTileY = Mathf.CeilToInt(Mathf.Min(a.y, b.y) - 1f);
        int maxTileY = Mathf.FloorToInt(Mathf.Max(a.y, b.y));
        if ((long)maxTileX - minTileX > 128 || (long)maxTileY - minTileY > 128)
            throw new InvalidDataException("UV Repeat 範圍過大。");

        for (int tileY = minTileY; tileY <= maxTileY; tileY++)
        for (int tileX = minTileX; tileX <= maxTileX; tileX++)
        {
            Vector2 start = a - new Vector2(tileX, tileY);
            Vector2 end = b - new Vector2(tileX, tileY);
            if (!ClipUvLine(ref start, ref end)) continue;
            int x0 = Mathf.Clamp(Mathf.RoundToInt(start.x * (width - 1)), 0, width - 1);
            int y0 = Mathf.Clamp(Mathf.RoundToInt(start.y * (height - 1)), 0, height - 1);
            int x1 = Mathf.Clamp(Mathf.RoundToInt(end.x * (width - 1)), 0, width - 1);
            int y1 = Mathf.Clamp(Mathf.RoundToInt(end.y * (height - 1)), 0, height - 1);
            DrawPixelLine(pixels, width, height, x0, y0, x1, y1);
        }
    }

    private static bool ClipUvLine(ref Vector2 a, ref Vector2 b)
    {
        float t0 = 0f, t1 = 1f;
        float dx = b.x - a.x, dy = b.y - a.y;
        if (!ClipTest(-dx, a.x, ref t0, ref t1) || !ClipTest(dx, 1f - a.x, ref t0, ref t1) ||
            !ClipTest(-dy, a.y, ref t0, ref t1) || !ClipTest(dy, 1f - a.y, ref t0, ref t1)) return false;
        Vector2 origin = a;
        a = origin + new Vector2(dx, dy) * t0;
        b = origin + new Vector2(dx, dy) * t1;
        return true;
    }

    private static bool ClipTest(float p, float q, ref float t0, ref float t1)
    {
        if (Mathf.Approximately(p, 0f)) return q >= 0f;
        float r = q / p;
        if (p < 0f) { if (r > t1) return false; if (r > t0) t0 = r; }
        else { if (r < t0) return false; if (r < t1) t1 = r; }
        return true;
    }

    private static void DrawPixelLine(Color32[] pixels, int width, int height, int x0, int y0, int x1, int y1)
    {
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int error = dx + dy;
        var black = new Color32(0, 0, 0, 255);
        while (true)
        {
            if (x0 >= 0 && x0 < width && y0 >= 0 && y0 < height) pixels[y0 * width + x0] = black;
            if (x0 == x1 && y0 == y1) break;
            int doubled = error * 2;
            if (doubled >= dy) { error += dy; x0 += sx; }
            if (doubled <= dx) { error += dx; y0 += sy; }
        }
    }

    private static bool IsFinite(Vector2 value)
    {
        return !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y);
    }

    private static string SafeFileName(string value)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(character => invalid.Contains(character) ? '_' : character).ToArray());
    }

    public void OnClickImportTexture()
    {
        if (!TryGetOptionSelection(nowSelectOption, out string sourceId, out Transform[] selected))
        {
            Debug.LogError("請先選擇 Head、Hair 或 Other。");
            return;
        }

        if (string.IsNullOrEmpty(sourceId) || selected == null || selected.Length == 0)
        {
            Debug.LogError("尚未選擇 " + OptionName(nowSelectOption) + " 模型。");
            return;
        }

        string role = GetClickedTextureRole(nowSelectOption, selected);
        if (role == null)
        {
            Debug.LogError("無法判斷要匯入主貼圖、OC 或 SPM。");
            return;
        }

        string[] paths = StandaloneFileBrowser.OpenFilePanel(
            "選擇 DDS 或 PNG 貼圖", "",
            new[] { new ExtensionFilter("貼圖", "dds", "png") }, false);
        if (paths == null || paths.Length == 0 || string.IsNullOrEmpty(paths[0])) return;

        Texture2D png = null;
        Texture2D preview = null;
        try
        {
            RawTexture original = FindCurrentTexture(nowSelectOption, sourceId, selected, role);
            var originalInfo = G4TextureParser.ParseDDS(original.Dds, original.Name);
            bool linear = role.EndsWith("oc", StringComparison.OrdinalIgnoreCase) ||
                role.EndsWith("spm", StringComparison.OrdinalIgnoreCase);
            byte[] importedDds;
            string extension = Path.GetExtension(paths[0]);
            if (extension.Equals(".dds", StringComparison.OrdinalIgnoreCase))
            {
                byte[] sourceDds = File.ReadAllBytes(paths[0]);
                var importedInfo = G4TextureParser.ParseDDS(sourceDds, paths[0]);
                if (originalInfo.Width != importedInfo.Width || originalInfo.Height != importedInfo.Height)
                    throw new InvalidDataException("DDS 尺寸必須是 " + originalInfo.Width + "x" + originalInfo.Height + "。");
                Color32[] basePixels = ReadDdsBasePixels(sourceDds, paths[0], linear);
                importedDds = EncodeBc7MipDds(basePixels, originalInfo.Width, originalInfo.Height, originalInfo.MipCount);
            }
            else if (extension.Equals(".png", StringComparison.OrdinalIgnoreCase))
            {
                png = new Texture2D(2, 2, TextureFormat.RGBA32, false, linear);
                if (!png.LoadImage(File.ReadAllBytes(paths[0]), false))
                    throw new InvalidDataException("無法讀取 PNG：" + paths[0]);
                if (png.width != originalInfo.Width || png.height != originalInfo.Height)
                    throw new InvalidDataException("PNG 尺寸必須是 " + originalInfo.Width + "x" + originalInfo.Height + "。");
                importedDds = EncodeBc7MipDds(png.GetPixels32(), png.width, png.height, originalInfo.MipCount);
            }
            else throw new InvalidDataException("只支援 DDS 或 PNG。");

            preview = CreatePreviewTexture(importedDds, original.Name, linear);
            ClearImportedTexture(nowSelectOption, role, false);
            var imported = new ImportedTexture
            {
                SourceId = sourceId,
                Role = role,
                Dds = importedDds,
                Preview = preview
            };
            preview = null;
            importedTextures.Add(ImportKey(nowSelectOption, role), imported);
            EnsurePreviewMaterials(nowSelectOption, selected);
            ApplyImportedPreviews(nowSelectOption);
            RefreshTextureImages(nowSelectOption);
            Debug.Log("貼圖已匯入：" + paths[0]);
        }
        catch (Exception ex)
        {
            if (preview != null) DestroyRuntimeObject(preview);
            Debug.LogException(ex);
        }
        finally
        {
            if (png != null) DestroyRuntimeObject(png);
        }
    }

    private static string ImportKey(int option, string role) => option + ":" + role.ToLowerInvariant();

    private static string GetClickedTextureRole(int option, Transform[] selected)
    {
        string baseRole = FindBaseRole(selected);
        if (baseRole == null) return null;
        GameObject selectedObject = EventSystem.current != null ? EventSystem.current.currentSelectedGameObject : null;
        if (selectedObject == null || selectedObject.name.Equals("TextureButton", StringComparison.OrdinalIgnoreCase)) return baseRole;
        if (selectedObject.name.Equals("OCButton", StringComparison.OrdinalIgnoreCase)) return baseRole + "oc";
        if (selectedObject.name.Equals("SPMButton", StringComparison.OrdinalIgnoreCase)) return baseRole + "spm";
        return null;
    }

    private RawTexture FindCurrentTexture(int option, string sourceId, Transform[] selected, string role)
    {
        string relativePath = FindFaceModel(sourceId);
        RawHeadAsset source = RawHeadAsset.Read(
            DumpDataManager.CommonPath, DumpDataManager.Dx11Path,
            relativePath, CancellationToken.None);
        if (source.TextureContainer == null) throw new FileNotFoundException("找不到 G4TX：" + sourceId);

        var names = new HashSet<string>(selected.Where(value => value != null).Select(value => value.name), StringComparer.OrdinalIgnoreCase);
        var hashes = new HashSet<uint>();
        foreach (var part in source.Preview.Parts)
        {
            if (!names.Contains(part.Name)) continue;
            foreach (uint hash in source.Preview.Materials[part.Material].TextureHashes) hashes.Add(hash);
        }
        RawTexture[] candidates = source.Textures
            .Where(texture => hashes.Contains(texture.Hash) && TextureRole(texture.Name) == role)
            .ToArray();
        if (candidates.Length != 1)
            throw new InvalidDataException(OptionName(option) + " 找不到唯一的 _" + role + " 貼圖，目前找到 " + candidates.Length + " 張。");
        return candidates[0];
    }

    private static string TextureRole(string name)
    {
        if (string.IsNullOrEmpty(name)) return null;
        Match match = Regex.Match(name, @"_(\d{2})(oc|spm)?(?:\.\d+|\.a)?$", RegexOptions.IgnoreCase);
        return match.Success ? (match.Groups[1].Value + match.Groups[2].Value).ToLowerInvariant() : null;
    }

    private static string BaseTextureRole(string name)
    {
        string role = TextureRole(name);
        return TextureChannel(role) == "base" ? role : null;
    }

    private static string TextureChannel(string role)
    {
        if (string.IsNullOrEmpty(role)) return null;
        if (role.EndsWith("spm", StringComparison.OrdinalIgnoreCase)) return "spm";
        if (role.EndsWith("oc", StringComparison.OrdinalIgnoreCase)) return "oc";
        return Regex.IsMatch(role, @"^\d{2}$") ? "base" : null;
    }

    private bool TryGetOptionSelection(int option, out string sourceId, out Transform[] selected)
    {
        sourceId = null;
        selected = null;
        if (option == 1)
        {
            sourceId = nowCharacterData.headId;
            selected = nowCharacterData.head;
            return true;
        }
        if (option == 2)
        {
            sourceId = nowCharacterData.hairId;
            selected = nowCharacterData.hair;
            return true;
        }
        if (option == 3)
        {
            sourceId = nowCharacterData.otherId;
            selected = nowCharacterData.other;
            return true;
        }
        return false;
    }

    private static string OptionName(int option)
    {
        if (option == 1) return "Head";
        if (option == 2) return "Hair";
        if (option == 3) return "Other";
        return "模型";
    }
    private static void ValidateImportedDimensions(G4TextureParser.TextureData expected, G4TextureParser.TextureData imported)
    {
        if (expected.Width != imported.Width || expected.Height != imported.Height)
            throw new InvalidDataException("DDS 尺寸必須是 " + expected.Width + "x" + expected.Height + "。");
        if (expected.MipCount != imported.MipCount)
            throw new InvalidDataException("DDS mip 數必須是 " + expected.MipCount + "。");
    }

    private static Texture2D CreatePreviewTexture(byte[] dds, string name, bool linear)
    {
        var data = G4TextureParser.ParseDDS(dds, name);
        if (!SystemInfo.SupportsTextureFormat(data.Format))
            throw new InvalidDataException("目前 GPU 不支援 " + data.Format + "。");
        var texture = new Texture2D(data.Width, data.Height, data.Format, data.MipCount, linear)
        {
            name = name,
            wrapMode = TextureWrapMode.Repeat,
            filterMode = FilterMode.Bilinear
        };
        texture.LoadRawTextureData(data.Pixels);
        texture.Apply(false, true);
        return texture;
    }

    private static Color32[] ReadDdsBasePixels(byte[] dds, string name, bool linear)
    {
        Texture2D source = null;
        Texture2D readback = null;
        RenderTexture render = null;
        RenderTexture previous = RenderTexture.active;
        try
        {
            source = CreatePreviewTexture(dds, name, linear);
            render = RenderTexture.GetTemporary(source.width, source.height, 0, RenderTextureFormat.ARGB32, linear ? RenderTextureReadWrite.Linear : RenderTextureReadWrite.sRGB);
            Graphics.Blit(source, render);
            RenderTexture.active = render;
            readback = new Texture2D(source.width, source.height, TextureFormat.RGBA32, false, false);
            readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            readback.Apply(false, false);
            return readback.GetPixels32();
        }
        finally
        {
            RenderTexture.active = previous;
            if (render != null) RenderTexture.ReleaseTemporary(render);
            if (source != null) DestroyRuntimeObject(source);
            if (readback != null) DestroyRuntimeObject(readback);
        }
    }

    private static byte[] EncodeBc7MipDds(Color32[] basePixels, int width, int height, int mipCount)
    {
        var levels = new List<byte[]>();
        Color32[] pixels = basePixels;
        int levelWidth = width, levelHeight = height;
        for (int mip = 0; mip < mipCount; mip++)
        {
            byte[] encoded = Bc7PortraitEncoder.Encode(pixels, levelWidth, levelHeight, CancellationToken.None);
            levels.Add(encoded.Skip(148).ToArray());
            if (mip + 1 < mipCount)
            {
                pixels = Downsample(pixels, levelWidth, levelHeight);
                levelWidth = Math.Max(1, levelWidth / 2);
                levelHeight = Math.Max(1, levelHeight / 2);
            }
        }

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("DDS "));
            writer.Write(124); writer.Write(0xA1007); writer.Write(height); writer.Write(width);
            writer.Write(((width + 3) / 4) * ((height + 3) / 4) * 16);
            writer.Write(0); writer.Write(mipCount); writer.Write(new byte[44]);
            writer.Write(32); writer.Write(4); writer.Write(System.Text.Encoding.ASCII.GetBytes("DX10")); writer.Write(new byte[20]);
            writer.Write(mipCount > 1 ? 0x401008 : 0x1000); writer.Write(new byte[16]);
            writer.Write(98); writer.Write(3); writer.Write(0); writer.Write(1); writer.Write(0);
            foreach (byte[] level in levels) writer.Write(level);
            return stream.ToArray();
        }
    }

    private static Color32[] Downsample(Color32[] source, int width, int height)
    {
        int outputWidth = Math.Max(1, width / 2), outputHeight = Math.Max(1, height / 2);
        var output = new Color32[outputWidth * outputHeight];
        for (int y = 0; y < outputHeight; y++)
        for (int x = 0; x < outputWidth; x++)
        {
            int r = 0, g = 0, b = 0, a = 0, count = 0;
            for (int oy = 0; oy < 2; oy++)
            for (int ox = 0; ox < 2; ox++)
            {
                int sx = Math.Min(width - 1, x * 2 + ox), sy = Math.Min(height - 1, y * 2 + oy);
                Color32 color = source[sy * width + sx];
                r += color.r; g += color.g; b += color.b; a += color.a; count++;
            }
            output[y * outputWidth + x] = new Color32((byte)(r / count), (byte)(g / count), (byte)(b / count), (byte)(a / count));
        }
        return output;
    }

    private void EnsurePreviewMaterials(int option, Transform[] selected)
    {
        if (previewMaterialBindings.ContainsKey(option)) return;
        var bindings = new List<TextureMaterialBinding>();
        foreach (Transform part in selected)
        {
            if (part == null) continue;
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                Material original = renderer.sharedMaterial;
                if (original == null) continue;
                var replacement = new Material(original);
                renderer.sharedMaterial = replacement;
                bindings.Add(new TextureMaterialBinding
                {
                    Renderer = renderer,
                    Original = original,
                    Replacement = replacement
                });
            }
        }
        previewMaterialBindings[option] = bindings;
    }

    private void ApplyImportedPreviews(int option)
    {
        if (!previewMaterialBindings.TryGetValue(option, out var bindings)) return;
        string baseRole = bindings.Select(value => value.Original)
            .Where(value => value != null)
            .Select(value => value.mainTexture)
            .Where(value => value != null)
            .Select(value => BaseTextureRole(value.name))
            .FirstOrDefault(value => value != null);
        foreach (var binding in bindings)
        {
            if (binding.Replacement == null || binding.Original == null) continue;
            binding.Replacement.mainTexture = binding.Original.mainTexture;
            binding.Replacement.SetTexture("_OcTex", binding.Original.GetTexture("_OcTex"));
            binding.Replacement.SetTexture("_SpmTex", binding.Original.GetTexture("_SpmTex"));
        }
        if (baseRole != null)
        {
            ApplyPreviewRole(option, baseRole, bindings, "_MainTex");
            ApplyPreviewRole(option, baseRole + "oc", bindings, "_OcTex");
            ApplyPreviewRole(option, baseRole + "spm", bindings, "_SpmTex");
        }
    }

    private void ApplyPreviewRole(int option, string role, List<TextureMaterialBinding> bindings, string property)
    {
        if (!importedTextures.TryGetValue(ImportKey(option, role), out var imported)) return;
        foreach (var binding in bindings)
            if (binding.Replacement != null)
                binding.Replacement.SetTexture(property, imported.Preview);
    }

    private void RefreshTextureImages(int option)
    {
        if (!TryGetOptionSelection(option, out _, out Transform[] selected)) return;
        Material[] materials = selected == null ? Array.Empty<Material>() : selected
            .Where(value => value != null)
            .SelectMany(value => value.GetComponentsInChildren<Renderer>(true))
            .Select(renderer => renderer.sharedMaterial)
            .Where(value => value != null)
            .ToArray();
        string baseRole = FindBaseRole(selected);
        SetTextureImage(textureImages, option, FindMaterialTexture(materials, "_MainTex", baseRole));
        SetTextureImage(ocImages, option, FindMaterialTexture(materials, "_OcTex", baseRole == null ? null : baseRole + "oc"));
        SetTextureImage(spmImages, option, FindMaterialTexture(materials, "_SpmTex", baseRole == null ? null : baseRole + "spm"));
    }

    private static Texture FindMaterialTexture(IEnumerable<Material> materials, string property, string role)
    {
        foreach (Material material in materials)
        {
            Texture texture = material.GetTexture(property);
            if (texture != null && (role == null || TextureRole(texture.name) == role)) return texture;
        }
        return null;
    }

    private static string FindBaseRole(Transform[] selected)
    {
        if (selected == null) return null;
        foreach (Transform part in selected)
        {
            if (part == null) continue;
            foreach (Renderer renderer in part.GetComponentsInChildren<Renderer>(true))
            {
                Texture texture = renderer.sharedMaterial != null ? renderer.sharedMaterial.mainTexture : null;
                string role = texture != null ? BaseTextureRole(texture.name) : null;
                if (role != null) return role;
            }
        }
        return null;
    }
    private static void SetTextureImage(List<RawImage> images, int option, Texture texture)
    {
        if (option < 0 || option >= images.Count || images[option] == null) return;
        images[option].texture = texture;
        images[option].uvRect = new Rect(0f, 1f, 1f, -1f);
    }

    private void ClearImportedTexture(int option, string role, bool refreshImages)
    {
        string key = ImportKey(option, role);
        if (importedTextures.TryGetValue(key, out var imported))
        {
            if (imported.Preview != null) DestroyRuntimeObject(imported.Preview);
            importedTextures.Remove(key);
        }
        ApplyImportedPreviews(option);
        if (refreshImages) RefreshTextureImages(option);
    }

    private void ClearImportedTextures(int option, bool refreshImages)
    {
        string prefix = option + ":";
        foreach (string key in importedTextures.Keys.Where(value => value.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            if (importedTextures[key].Preview != null) DestroyRuntimeObject(importedTextures[key].Preview);
            importedTextures.Remove(key);
        }
        if (previewMaterialBindings.TryGetValue(option, out var bindings))
        {
            foreach (var binding in bindings)
            {
                if (binding.Renderer != null) binding.Renderer.sharedMaterial = binding.Original;
                if (binding.Replacement != null) DestroyRuntimeObject(binding.Replacement);
            }
            previewMaterialBindings.Remove(option);
        }
        if (refreshImages) RefreshTextureImages(option);
    }

    private byte[] ApplySelectedTextures(
        byte[] metadata,
        byte[] geometry,
        byte[] container,
        IReadOnlyDictionary<int, RawHeadAsset> optionSources)
    {
        G4ModelParser.Model model = G4ModelParser.Parse(metadata, geometry, "輸出模型");
        byte[] output = container;
        foreach (var entry in optionSources.OrderBy(value => value.Key))
        {
            int option = entry.Key;
            RawHeadAsset source = entry.Value;
            TryGetOptionSelection(option, out string sourceId, out Transform[] selected);
            var selectedNames = new HashSet<string>(
                (selected ?? Array.Empty<Transform>()).Where(value => value != null).Select(value => value.name),
                StringComparer.OrdinalIgnoreCase);
            var sourceHashes = new HashSet<uint>();
            foreach (var part in source.Preview.Parts.Where(value => selectedNames.Contains(value.Name)))
                foreach (uint hash in source.Preview.Materials[part.Material].TextureHashes)
                    sourceHashes.Add(hash);

            var destinationHashes = new HashSet<uint>();
            foreach (var part in OutputPartsForOption(model, option))
            {
                if (part.Material < 0 || part.Material >= model.Materials.Count)
                    throw new InvalidDataException(OptionName(option) + " 的輸出材質索引無效。");
                foreach (uint hash in model.Materials[part.Material].TextureHashes)
                    destinationHashes.Add(hash);
            }

            foreach (string channel in new[] { "base", "oc", "spm" })
            {
                RawTexture[] candidates = source.Textures
                    .Where(value => sourceHashes.Contains(value.Hash) && TextureChannel(TextureRole(value.Name)) == channel)
                    .ToArray();
                if (candidates.Length == 0) continue;
                if (candidates.Length != 1)
                    throw new InvalidDataException(OptionName(option) + " 找到多張 " + channel + " 貼圖。");
                RawTexture sourceTexture = candidates[0];
                string role = TextureRole(sourceTexture.Name);
                byte[] dds = sourceTexture.Dds;
                if (importedTextures.TryGetValue(ImportKey(option, role), out var imported) &&
                    imported.SourceId.Equals(sourceId, StringComparison.OrdinalIgnoreCase))
                    dds = imported.Dds;
                output = ReplaceG4TxTexture(
                    output, channel, dds, sourceTexture.Descriptor, destinationHashes);
            }
        }
        return output;
    }

    private static IEnumerable<G4ModelParser.Part> OutputPartsForOption(G4ModelParser.Model model, int option)
    {
        if (option == 1)
            return model.Parts.Where(value => IsFacePart(value.Name));
        G4ModelParser.Part[] nonFace = model.Parts.Where(value => !IsFacePart(value.Name)).ToArray();
        int index = option == 2 ? 0 : 1;
        return index < nonFace.Length ? new[] { nonFace[index] } : Array.Empty<G4ModelParser.Part>();
    }

    private static bool IsFacePart(string name)
    {
        return name.StartsWith("eye_", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("mouth_", StringComparison.OrdinalIgnoreCase);
    }

    private static byte[] ReplaceG4TxTexture(
        byte[] container,
        string channel,
        byte[] dds,
        byte[] sourceDescriptor,
        HashSet<uint> materialHashes)
    {
        List<RawTexture> textures = RawTexture.Read(container, "匯出 G4TX");
        int[] matches = textures.Select((texture, index) => new { texture, index })
            .Where(value => materialHashes.Contains(value.texture.Hash) &&
                TextureChannel(TextureRole(value.texture.Name)) == channel)
            .Select(value => value.index).ToArray();
        if (matches.Length != 1)
            throw new InvalidDataException("輸出 G4TX 必須剛好找到一張 " + channel + " 貼圖，目前找到 " + matches.Length + " 張。");
        int index = matches[0];
        G4TextureParser.ParseDDS(dds, "匯入 " + channel);
        textures[index].Dds = dds;
        if (sourceDescriptor == null || sourceDescriptor.Length != 48)
            throw new InvalidDataException("來源 G4TX descriptor 無效。");
        textures[index].Descriptor = (byte[])sourceDescriptor.Clone();
        return WriteG4TxPreservingTable(container, textures);
    }

    private static byte[] WriteG4TxPreservingTable(byte[] template, IReadOnlyList<RawTexture> textures)
    {
        if (template == null || template.Length < 0x60)
            throw new InvalidDataException("G4TX 範本太小。");
        int count = BitConverter.ToUInt16(template, 0x20);
        if (count != textures.Count)
            throw new InvalidDataException("G4TX 貼圖數量與原生表格不一致。");
        int payload = checked((0x60 + BitConverter.ToInt32(template, 12) + 15) & ~15);
        if (payload < 0x60 || payload > template.Length)
            throw new InvalidDataException("G4TX payload offset 無效。");

        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(template, 0, payload);
            for (int i = 0; i < textures.Count; i++)
            {
                while (stream.Position % 16 != 0) writer.Write((byte)0);
                int objectOffset = checked((int)stream.Position - payload);
                writer.Write(textures[i].Dds);
                int objectSize = textures[i].Dds.Length;
                long end = stream.Position;
                stream.Position = 0x60 + i * 48;
                writer.Write(textures[i].Descriptor);
                stream.Position = 0x64 + i * 48;
                writer.Write(objectOffset);
                writer.Write(objectSize);
                stream.Position = end;
            }
            while (stream.Position % 16 != 0) writer.Write((byte)0);
            int outputSize = checked((int)stream.Length);
            stream.Position = 0x2c;
            writer.Write(outputSize - payload);
            byte[] output = stream.ToArray();
            RawTexture.Read(output, "匯出 G4TX 驗證");
            return output;
        }
    }

    private static void DestroyRuntimeObject(UnityEngine.Object value)
    {
        if (value == null) return;
        if (Application.isPlaying) Destroy(value); else DestroyImmediate(value);
    }

    private void OnDestroy()
    {
        ClearImportedTextures(1, false);
        ClearImportedTextures(2, false);
        ClearImportedTextures(3, false);
    }
}
