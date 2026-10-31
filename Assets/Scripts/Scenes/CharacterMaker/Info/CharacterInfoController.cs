using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G4Composition;
using G4Runtime;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.UI;

public partial class CharacterInfoController : MonoBehaviour
{
    public CharacterManager manager;
    CharacterData nowCharacterData => manager.nowCharacterData;
    Camera charIconCamera => manager.charIconCamera;
    AvatarSettingController avatarSetting => manager.avatarSetting;
    bool initialized;

    internal void Initialize(bool dumpReady)
    {
        if (initialized) return;
        initialized = true;
        InitializeDetails(dumpReady); InitializeSkills(dumpReady); InitializeNewControls();
        BindBodyTypeButtons(); BindUniformCollarButtons(); BindEquipmentButtons(); BindChestSizeButtons();
        RefreshBodySettingButtons(); ExcludeBodyLayerFromIconCamera();
        if (dumpReady) LoadDefaultBodyPreviews();
        if (SkinColor != null) { appliedSkinColor = SkinColor.color; hasAppliedSkinColor = true; }
    }

    internal void RefreshAvailability()
    {
        if (avatarCharInfoParent != null) avatarCharInfoParent.gameObject.SetActive(manager.HasAvatarInfo());
    }

    void Update()
    {
        if (manager == null) return;
        RefreshAvailability();
        if (initialized && identityCatalog != null && teamLocale != AppLocale()) RefreshIdentityFields();
        if (!initialized || SkinColor == null) return;
        Color color = SkinColor.color;
        if (hasAppliedSkinColor && ApproximatelySameColor(appliedSkinColor, color)) return;
        appliedSkinColor = color; hasAppliedSkinColor = true;
        ApplySkinColorToBodyPreviews(color);
    }

    void OnDestroy() { CancelIdentityTranslation(); ClearBodyPreviews(); }
    public Transform bodyParent;

    public Transform bodyTypeParent;

    private readonly List<Button> bodyTypeButtons = new();
    private readonly List<Button> uniformCollarButtons = new();
    private readonly List<Button> equipmentButtons = new();
    private readonly List<Button> sleeveButtons = new();
    private readonly Dictionary<int, int> previewFashionKeys = new();
    private readonly List<Button> chestSizeButtons = new();
    private readonly Dictionary<int, GameObject> bodyPreviews = new();
    private readonly Dictionary<Vector2Int, float> iconBodyWidths = new();
    private CfgTable bodyBaseTable;
    internal CfgTable bodyModelTable;
    private string bodyBaseTablePath;
    private string bodyModelTablePath;
    private CfgTable.Row selectedCharacterRow;
    private CfgTable.Row selectedModelRow;
    internal CfgTable.Row selectedOriginalBodyRow;
    private string previewUniformStem;

    public Image SkinColor;
    private Color appliedSkinColor;
    private bool hasAppliedSkinColor;

    public Transform avatarCharInfoParent;

    private void BindBodyTypeButtons()
    {
        bodyTypeButtons.Clear();
        if (bodyTypeParent == null) return;
        Transform buttons = bodyTypeParent.Find("BodyType/btns");
        if (buttons == null && bodyTypeParent.name == "BodyType") buttons = bodyTypeParent.Find("btns");
        if (buttons == null) buttons = bodyTypeParent;
        for (int i = 0; i < buttons.childCount && bodyTypeButtons.Count < 8; i++)
        {
            Button button = buttons.GetChild(i).GetComponent<Button>();
            if (button == null) continue;
            int bodyType = bodyTypeButtons.Count + 1;
            button.onClick.AddListener(() => OnClickBodyType(bodyType));
            bodyTypeButtons.Add(button);
        }
        if (bodyTypeButtons.Count != 8)
            Debug.LogWarning("Body Type 需要 8 個按鈕，目前找到 " + bodyTypeButtons.Count + " 個。");
    }

    private void OnClickBodyType(int bodyType)
    {
        if (bodyType < 1 || bodyType > 8) return;
        if (bodyModelTable == null) return;
        if (!NativeBodyTypes.CanSelect(bodyModelTable, bodyType, CurrentGender)) return;
        var selected = NativeBodyTypes.ResolveForGender(bodyModelTable, bodyType, 0, CurrentGender, selectedOriginalBodyRow);
        if (selected == null) return;
        bool variantChanged = nowCharacterData.bodyVariantId != selected.UInt(0);
        nowCharacterData.bodyType = bodyType;
        nowCharacterData.bodyVariantId = selected.UInt(0);
        nowCharacterData.bodyAvatarCode = null;
        if (variantChanged) { ClearBodyPreviews(); PreloadBodyPreviews(); }
        ShowBodyPreview(bodyType);
        RefreshBodySettingButtons();
    }

    private void BindUniformCollarButtons()
    {
        BindBodySettingButtons("UniformCollar", 2, uniformCollarButtons, value =>
        {
            nowCharacterData.bodyDetailFlags =
                (nowCharacterData.bodyDetailFlags & unchecked((int)0xffff0000)) | value;
            foreach (GameObject preview in bodyPreviews.Values)
                if (preview != null) ConfigureBodyPreview(preview);
            RefreshBodySettingButtons();
        });
    }

    private void BindChestSizeButtons()
    {
        BindBodySettingButtons("ChestSize", 3, chestSizeButtons, value =>
        {
            if (nowCharacterData.chestSize == value) return;
            nowCharacterData.chestSize = value;
            // All three meshes are baked once while loading a body variant.
            // Changing chest size only switches the cached preview.
            foreach (var preview in bodyPreviews.Values) ConfigureBodyPreview(preview);
            ShowBodyPreview(nowCharacterData.bodyType);
            RefreshBodySettingButtons();
        });
    }

    private void BindEquipmentButtons()
    {
        BindBodySettingButtons("UniformHem", 3, equipmentButtons, value =>
        {
            if (!CanUseFashion(0, value)) return;
            nowCharacterData.bodyDetailFlags = (nowCharacterData.bodyDetailFlags & 0xffff) | (value << 16);
            ShowBodyPreview(nowCharacterData.bodyType);
            RefreshBodySettingButtons();
        });
        BindBodySettingButtons("UniformSleeve", 2, sleeveButtons, value =>
        {
            if (!CanUseFashion(value, 0)) return;
            nowCharacterData.uniformSleeve = value;
            ShowBodyPreview(nowCharacterData.bodyType);
            RefreshBodySettingButtons();
        });
    }

    private bool CanUseFashion(int sleeve, int hem)
    {
        var body = bodyModelTable == null ? null : FindBodyRow(nowCharacterData.bodyType);
        return body != null && NativeUniformFashion.Allowed(body, sleeve, hem) &&
            NativeUniformFashion.Resolve(previewUniformStem + "_" + DefaultUniformMember +
                (sleeve != 0 ? "_slv" : hem == 2 ? "_blyR" : ""), body) != null;
    }

    private void NormalizeFashion()
    {
        if (!CanUseFashion(nowCharacterData.uniformSleeve, 0)) nowCharacterData.uniformSleeve = 0;
        int hem = (nowCharacterData.bodyDetailFlags >> 16) & 0xffff;
        if (!CanUseFashion(0, hem)) nowCharacterData.bodyDetailFlags &= 0xffff;
    }

    private void BindBodySettingButtons(
        string groupName,
        int expectedCount,
        List<Button> destination,
        UnityAction<int> callback)
    {
        destination.Clear();
        if (bodyTypeParent == null) return;
        Transform group = bodyTypeParent.Find(groupName);
        if (group == null) return;
        Transform buttons = group.Find("btns") ?? group;
        for (int i = 0; i < buttons.childCount && destination.Count < expectedCount; i++)
        {
            Button button = buttons.GetChild(i).GetComponent<Button>();
            if (button == null) continue;
            int value = destination.Count;
            button.onClick.AddListener(() => callback(value));
            destination.Add(button);
        }
        if (destination.Count != expectedCount)
            Debug.LogWarning(groupName + " 需要 " + expectedCount + " 個按鈕，目前找到 " + destination.Count + " 個。");
    }

    internal void RefreshBodySettingButtons()
    {
        SetSelectedButton(bodyTypeButtons, nowCharacterData.bodyType - 1);
        if (bodyModelTable != null)
            for (int i = 0; i < bodyTypeButtons.Count; i++)
                bodyTypeButtons[i].interactable &= NativeBodyTypes.CanSelect(bodyModelTable, i + 1, CurrentGender);
        SetSelectedButton(uniformCollarButtons, nowCharacterData.bodyDetailFlags & 0xffff);
        SetSelectedButton(equipmentButtons, (nowCharacterData.bodyDetailFlags >> 16) & 0xffff);
        SetSelectedButton(sleeveButtons, nowCharacterData.uniformSleeve);
        for (int i = 0; i < sleeveButtons.Count; i++) sleeveButtons[i].interactable = i != nowCharacterData.uniformSleeve && CanUseFashion(i, 0);
        for (int i = 0; i < equipmentButtons.Count; i++) equipmentButtons[i].interactable = i != ((nowCharacterData.bodyDetailFlags >> 16) & 0xffff) && CanUseFashion(0, i);
        SetSelectedButton(chestSizeButtons, nowCharacterData.chestSize);
    }

    private static void SetSelectedButton(List<Button> buttons, int selectedIndex)
    {
        Color selectedColor = new Color(0.55f, 0.55f, 0.55f, 1f);
        for (int i = 0; i < buttons.Count; i++)
            if (buttons[i] != null)
            {
                buttons[i].interactable = i != selectedIndex;
                if (buttons[i].image != null) buttons[i].image.color = i == selectedIndex ? selectedColor : Color.white;
            }
    }

    internal void LoadCharacterBody(string characterId)
    {
        ClearBodyPreviews();
        nowCharacterData.originalBodyType = nowCharacterData.bodyType = 1;
        nowCharacterData.originalChestSize = nowCharacterData.chestSize = -1;
        nowCharacterData.originalBodyDetailFlags = nowCharacterData.bodyDetailFlags = 0;
        selectedCharacterRow = selectedModelRow = selectedOriginalBodyRow = null;
        nowCharacterData.bodyVariantId = nowCharacterData.originalBodyVariantId = 0;
        nowCharacterData.uniformSleeve = nowCharacterData.originalUniformSleeve = 0;
        nowCharacterData.bodyAvatarCode = null;
        if (bodyParent == null || string.IsNullOrWhiteSpace(characterId)) return;
        try
        {
            LoadBodyTables();
            selectedCharacterRow = bodyBaseTable.Rows.SingleOrDefault(row =>
                row.Name == "CHARA_BASE_INFO" && row.Values.Length > 6 &&
                string.Equals(row.Text(1), characterId, StringComparison.OrdinalIgnoreCase));
            if (selectedCharacterRow == null)
                throw new InvalidDataException("找不到角色身體設定：" + characterId);
            uint modelId = selectedCharacterRow.UInt(6);
            selectedModelRow = bodyModelTable.Rows.SingleOrDefault(row =>
                row.Name == "CHARA_MODEL_INFO" && row.Values.Length > 31 && row.UInt(0) == modelId);
            if (selectedModelRow == null)
                throw new InvalidDataException(characterId + " 找不到 CHARA_MODEL_INFO：" + modelId.ToString("X8"));
            Color characterSkinColor = DecodeSkinColor(selectedModelRow.UInt(16));
            if (SkinColor != null) SkinColor.color = characterSkinColor;
            appliedSkinColor = characterSkinColor;
            hasAppliedSkinColor = true;
            uint bodyId = selectedModelRow.UInt(4);
            selectedOriginalBodyRow = bodyModelTable.Rows.FirstOrDefault(row =>
                row.Values != null && row.Values.Length >= 7 && row.Types != null &&
                row.Types.Length >= 7 && row.Types[0] == 1 && row.UInt(0) == bodyId);
            if (selectedOriginalBodyRow == null)
                throw new InvalidDataException(characterId + " 找不到 CHARA_BODY_INFO：" + bodyId.ToString("X8"));
            if (!IsBodyInfoRow(selectedOriginalBodyRow))
                throw new InvalidDataException(characterId + " 的身體資料 " + bodyId.ToString("X8") +
                    " 沒有有效的 _uniform/sh*.g4md 路徑。");

            int bodyType = NativeBodyTypes.FromRow(selectedOriginalBodyRow);
            if (bodyType < 1 || bodyType > 8) bodyType = 1;
            int chest = NativeBodyShape.FromNativeChestSize(checked((int)selectedModelRow.Int(31)));
            int details = unchecked((int)(selectedModelRow.UInt(12) & 0xffffu) |
                (int)((selectedModelRow.UInt(13) & 0xffffu) << 16));
            nowCharacterData.originalBodyType = nowCharacterData.bodyType = bodyType;
            nowCharacterData.originalBodyVariantId = nowCharacterData.bodyVariantId = selectedOriginalBodyRow.UInt(0);
            nowCharacterData.originalChestSize = nowCharacterData.chestSize = chest;
            string uniformName = NativeUniformFashion.ModelName(selectedModelRow.UInt(5));
            nowCharacterData.originalUniformSleeve = nowCharacterData.uniformSleeve = uniformName != null && uniformName.EndsWith("_slv", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            if (uniformName != null && uniformName.EndsWith("_blyR", StringComparison.OrdinalIgnoreCase)) details = (details & 0xffff) | (2 << 16);
            nowCharacterData.originalBodyDetailFlags = nowCharacterData.bodyDetailFlags = details;
            previewUniformStem = FindFirstUniformStem();
            PreloadBodyPreviews();

            ShowBodyPreview(bodyType);
            RefreshBodySettingButtons();
        }
        catch (Exception exception)
        {
            ClearBodyPreviews();
            LoadDefaultBodyPreviews();
            Debug.LogWarning(characterId + " 無法讀取原角色身體設定，已使用預設 Body Type 1：" + exception.Message);
        }
    }

    private void LoadDefaultBodyPreviews()
    {
        if (bodyParent == null || string.IsNullOrEmpty(DumpDataManager.CommonPath) ||
            string.IsNullOrEmpty(DumpDataManager.Dx11Path)) return;
        ClearBodyPreviews();
        nowCharacterData.originalBodyType = nowCharacterData.bodyType = 1;
        nowCharacterData.originalChestSize = nowCharacterData.chestSize = -1;
        nowCharacterData.originalBodyDetailFlags = nowCharacterData.bodyDetailFlags = 0;
        try
        {
            LoadBodyTables();
            previewUniformStem = FindFirstUniformStem();
            PreloadBodyPreviews();
            ShowBodyPreview(1);
            RefreshBodySettingButtons();
        }
        catch (Exception exception)
        {
            ClearBodyPreviews();
            Debug.LogWarning("預設身體模型載入失敗：" + exception.Message);
        }
    }

    private void LoadBodyTables()
    {
        string directory = Path.Combine(DumpDataManager.CommonPath, "gamedata", "character");
        bodyBaseTablePath = FindVersionedTable(directory, "chara_base_");
        bodyModelTablePath = FindVersionedTable(directory, "chara_model_");
        bodyBaseTable = CfgTable.Read(File.ReadAllBytes(bodyBaseTablePath), bodyBaseTablePath);
        bodyModelTable = CfgTable.Read(File.ReadAllBytes(bodyModelTablePath), bodyModelTablePath);
    }

    private static string FindVersionedTable(string directory, string prefix)
    {
        string[] paths = Directory.GetFiles(directory, prefix + "*.cfg.bin")
            .Where(path => Regex.IsMatch(Path.GetFileName(path), "^" + Regex.Escape(prefix) + @"\d"))
            .ToArray();
        if (paths.Length != 1)
            throw new InvalidDataException("無法唯一判定資料表 " + prefix + "，找到 " + paths.Length + " 個。");
        return paths[0];
    }

    internal const string DefaultUniformStem = "u117401";
    internal const string DefaultUniformMember = "10";

    private static string FindFirstUniformStem()
    {
        const string uniformStem = DefaultUniformStem;
        string path = Path.Combine(DumpDataManager.Dx11Path, "chr", "_uniform", "u000101",
            uniformStem + "_10.g4tx");
        if (!File.Exists(path))
            throw new FileNotFoundException("找不到簡約隊服貼圖。", path);
        return uniformStem;
    }

    private void PreloadBodyPreviews()
    {
        for (int bodyType = 1; bodyType <= 8; bodyType++) LoadBodyPreview(bodyType);
    }

    private void LoadBodyPreview(int bodyType)
    {
        var body = FindBodyRow(bodyType);
        if (body == null) return;
        int sleeve = NativeUniformFashion.Allowed(body, nowCharacterData.uniformSleeve, 0) ? nowCharacterData.uniformSleeve : 0;
        int hem = (nowCharacterData.bodyDetailFlags >> 16) & 0xffff;
        if (!NativeUniformFashion.Allowed(body, 0, hem)) hem = 0;
        string name = previewUniformStem + "_" + DefaultUniformMember;
        var uniform = NativeUniformFashion.Resolve(name + (sleeve != 0 ? "_slv" : hem == 2 ? "_blyR" : ""), body);
        if (uniform == null) { sleeve = 0; hem = 0; uniform = NativeUniformFashion.Resolve(name, body); }
        if (uniform == null) throw new InvalidDataException("Missing uniform for body type " + bodyType);
        var preview = CreateBodyPreview(bodyType, body, uniform, hem);
        ConfigureBodyPreview(preview);
        preview.SetActive(false);
        if (bodyPreviews.TryGetValue(bodyType, out var old) && old != null) CharacterManager.DestroyRuntimeObject(old);
        bodyPreviews[bodyType] = preview;
        previewFashionKeys[bodyType] = sleeve * 10 + hem;
    }

    private GameObject CreateBodyPreview(int bodyType, CfgTable.Row bodyRow, NativeUniformFashion.Selection outfit, int hem)
    {
        var root = new GameObject("BodyType" + bodyType);
        root.transform.SetParent(bodyParent, false);
        try
        {
            var rig = NativeBodyShape.Skeleton(bodyRow);
            for (int chest = -1; chest < 3; chest++)
            {
                var chestRoot = new GameObject("ChestSize" + chest);
                chestRoot.transform.SetParent(root.transform, false);
                var pose = NativeBodyShape.Outfit(bodyRow, chest, hem);
                GameObject Load(string model, string texture)
                {
                    string folder = Path.Combine("chr", Path.GetDirectoryName(model));
                    return G4ModelLoader.CreateModel(folder, Path.GetFileNameWithoutExtension(model),
                        Path.GetFileNameWithoutExtension(texture), Path.Combine("chr", Path.GetDirectoryName(texture)),
                        boneTransforms: pose, skeletonOverride: rig, headLocalSkinning: false);
                }
                var uniform = Load(outfit.Uniform, outfit.UniformTexture);
                uniform.transform.SetParent(chestRoot.transform, false);
                var skin = Load(outfit.Skin, outfit.SkinTexture);
                skin.transform.SetParent(chestRoot.transform, false);
                Color skinColor = GetSelectedSkinColor();
                TintSkinRenderers(skin, skinColor, false);
                TintSkinRenderers(uniform, skinColor, true);
                iconBodyWidths[new Vector2Int(bodyType, chest)] = MeasureIconBodyWidth(uniform);
                chestRoot.SetActive(chest == nowCharacterData.chestSize);
            }
            return root;
        }
        catch { CharacterManager.DestroyRuntimeObject(root); throw; }
    }

    internal float GetIconBodyWidth(int bodyType, int chestSize) =>
        iconBodyWidths.TryGetValue(new Vector2Int(bodyType, chestSize), out float width) ? width : 0;

    static float MeasureIconBodyWidth(GameObject uniform)
    {
        // Use the baked torso below the sleeves so T-pose arms cannot inflate it.
        var filter = uniform.GetComponentsInChildren<MeshFilter>(true)
            .FirstOrDefault(f => f.name.EndsWith("_20", StringComparison.OrdinalIgnoreCase));
        if (filter == null || filter.sharedMesh == null) return 0;
        var vertices = filter.sharedMesh.vertices;
        Bounds bounds = filter.sharedMesh.bounds;
        float low = bounds.min.y + bounds.size.y * .35f, high = bounds.min.y + bounds.size.y * .55f;
        float left = float.PositiveInfinity, right = float.NegativeInfinity;
        foreach (var vertex in vertices)
            if (vertex.y >= low && vertex.y <= high) { left = Mathf.Min(left, vertex.x); right = Mathf.Max(right, vertex.x); }
        return right > left ? (right - left) * Mathf.Abs(filter.transform.lossyScale.x / uniform.transform.lossyScale.x) : bounds.size.x;
    }

    internal Color GetSelectedSkinColor()
    {
        if (SkinColor != null) return SkinColor.color;
        uint rgba = selectedModelRow != null && selectedModelRow.Values.Length > 16
            ? selectedModelRow.UInt(16) : 0xFFE4D0FFu;
        return DecodeSkinColor(rgba);
    }

    private static Color DecodeSkinColor(uint rgba)
    {
        return new Color(
            ((rgba >> 24) & 0xff) / 255f,
            ((rgba >> 16) & 0xff) / 255f,
            ((rgba >> 8) & 0xff) / 255f,
            (rgba & 0xff) / 255f);
    }

    private static uint EncodeSkinColor(Color color)
    {
        Color32 rgba = color;
        return ((uint)rgba.r << 24) |
            ((uint)rgba.g << 16) |
            ((uint)rgba.b << 8) |
            rgba.a;
    }

    private void ApplySkinColorToBodyPreviews(Color skinColor)
    {
        foreach (GameObject preview in bodyPreviews.Values)
        {
            if (preview == null) continue;
            foreach (Renderer renderer in preview.GetComponentsInChildren<Renderer>(true))
            {
                Material material = renderer.sharedMaterial;
                if (material != null && material.HasProperty("_SkinColor"))
                    material.SetColor("_SkinColor", skinColor);
            }
        }
    }

    private static bool ApproximatelySameColor(Color left, Color right)
    {
        const float epsilon = 0.0001f;
        return Mathf.Abs(left.r - right.r) < epsilon &&
            Mathf.Abs(left.g - right.g) < epsilon &&
            Mathf.Abs(left.b - right.b) < epsilon &&
            Mathf.Abs(left.a - right.a) < epsilon;
    }

    private static void TintSkinRenderers(GameObject model, Color skinColor, bool uniformMaskOnly)
    {
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>(true))
        {
            Material material = renderer.sharedMaterial;
            if (material == null) continue;
            if (uniformMaskOnly)
            {
                if (material.HasProperty("_SkinColor")) material.SetColor("_SkinColor", skinColor);
            }
            else
            {
                if (material.HasProperty("_SkinColor")) material.SetColor("_SkinColor", skinColor);
                if (material.HasProperty("_WholeSkin")) material.SetFloat("_WholeSkin", 1f);
            }
        }
    }

    internal CfgTable.Row FindBodyRow(int bodyType)
    {
        if (bodyModelTable == null || !NativeBodyTypes.CanSelect(bodyModelTable, bodyType, CurrentGender)) return null;
        return NativeBodyTypes.ResolveForGender(bodyModelTable, bodyType,
            bodyType == nowCharacterData.bodyType ? nowCharacterData.bodyVariantId : 0, CurrentGender, selectedOriginalBodyRow);
    }

    int CurrentGender => Identity?.gender ?? (selectedCharacterRow != null ? (int)selectedCharacterRow.Int(11) : 1);

    internal void SynchronizeBodyGender()
    {
        if (bodyModelTable == null || selectedOriginalBodyRow == null) return;
        var body = NativeBodyTypes.ResolveForGender(bodyModelTable, nowCharacterData.bodyType,
            nowCharacterData.bodyVariantId, CurrentGender, selectedOriginalBodyRow);
        if (body == null) return;
        if (nowCharacterData.bodyVariantId != body.UInt(0)) nowCharacterData.bodyAvatarCode = null;
        nowCharacterData.bodyVariantId = body.UInt(0); nowCharacterData.bodyType = NativeBodyTypes.FromRow(body);
        NormalizeFashion(); ClearBodyPreviews(); PreloadBodyPreviews();
        ShowBodyPreview(nowCharacterData.bodyType); RefreshBodySettingButtons();
    }

    private static bool IsBodyInfoRow(CfgTable.Row row)
    {
        if (row == null || row.Values == null || row.Values.Length < 7 || row.Types.Length < 7)
            return false;
        if (row.Types[0] != 1 || row.Types[2] != 0 || row.Types[5] != 1 || row.Types[6] != 1)
            return false;
        string path = row.Text(2)?.Replace('\\', '/');
        return !string.IsNullOrWhiteSpace(path) &&
            path.StartsWith("_uniform/sh", StringComparison.OrdinalIgnoreCase) &&
            path.EndsWith(".g4md", StringComparison.OrdinalIgnoreCase);
    }

    private void ConfigureBodyPreview(GameObject preview)
    {
        int bodyLayer = LayerMask.NameToLayer("Body");
        if (bodyLayer < 0)
            Debug.LogWarning("找不到 Body layer，身體可能會被頭像相機拍到。");
        else SetLayerRecursively(preview.transform, bodyLayer);

        foreach (Transform variant in preview.transform)
            if (variant.name.StartsWith("ChestSize", StringComparison.Ordinal) && int.TryParse(variant.name.Substring(9), out int size))
                variant.gameObject.SetActive(size == nowCharacterData.chestSize);
        int collar = nowCharacterData.bodyDetailFlags & 0xffff;
        foreach (Transform child in preview.GetComponentsInChildren<Transform>(true))
        {
            if (child == preview.transform) continue;
            if (child.name.IndexOf("_LOD1", StringComparison.OrdinalIgnoreCase) >= 0 ||
                child.name.IndexOf("_LOD2", StringComparison.OrdinalIgnoreCase) >= 0)
                child.gameObject.SetActive(false);
            else if (child.name.Equals("collar_10", StringComparison.OrdinalIgnoreCase))
                child.gameObject.SetActive(collar == 0);
            else if (child.name.Equals("collar_11", StringComparison.OrdinalIgnoreCase))
                child.gameObject.SetActive(collar != 0);
        }
    }

    private void ExcludeBodyLayerFromIconCamera()
    {
        int bodyLayer = LayerMask.NameToLayer("Body");
        if (bodyLayer < 0)
        {
            Debug.LogWarning("找不到 Body layer，無法設定頭像相機過濾。");
            return;
        }
        if (charIconCamera != null)
            charIconCamera.cullingMask &= ~(1 << bodyLayer);
    }

    private static void SetLayerRecursively(Transform root, int layer)
    {
        if (root == null) return;
        root.gameObject.layer = layer;
        for (int i = 0; i < root.childCount; i++)
            SetLayerRecursively(root.GetChild(i), layer);
    }

    private void ShowBodyPreview(int bodyType)
    {
        NormalizeFashion();
        int fashion = nowCharacterData.uniformSleeve * 10 + ((nowCharacterData.bodyDetailFlags >> 16) & 0xffff);
        if (bodyModelTable != null && (!previewFashionKeys.TryGetValue(bodyType, out int cached) || cached != fashion))
            LoadBodyPreview(bodyType);
        foreach (var pair in bodyPreviews) pair.Value.SetActive(pair.Key == bodyType);
        if (!bodyPreviews.ContainsKey(bodyType) && bodyModelTable != null)
            Debug.LogWarning("Body Type " + bodyType + " 沒有可用的原生身體模型。");
        manager.UpdateTargetAnchorForBodyType(bodyType);
    }

    int previewNeckBodyType;
    uint previewNeckBodyVariant;
    Vector3 previewNeckLocal;
    bool hasPreviewNeck;

    public bool TryGetPreviewNeck(out Vector3 point)
    {
        point = default;
        if (bodyModelTable == null || bodyParent == null) return false;
        if (!hasPreviewNeck || previewNeckBodyType != nowCharacterData.bodyType || previewNeckBodyVariant != nowCharacterData.bodyVariantId)
        {
            var row = FindBodyRow(nowCharacterData.bodyType);
            if (row == null) return false;
            var rig = NativeBodyShape.Skeleton(row);
            if (!rig.ByHash.TryGetValue(G4TextureParser.Crc32("c_n_1_0"), out int neck)) return false;
            Vector3 native = rig.Bind[neck].MultiplyPoint3x4(Vector3.zero);
            previewNeckLocal = new Vector3(native.x, native.y, -native.z);
            previewNeckBodyType = nowCharacterData.bodyType;
            previewNeckBodyVariant = nowCharacterData.bodyVariantId;
            hasPreviewNeck = true;
        }
        point = bodyParent.TransformPoint(previewNeckLocal);
        return true;
    }


    private void ClearBodyPreviews()
    {
        foreach (GameObject preview in bodyPreviews.Values)
            if (preview != null) CharacterManager.DestroyRuntimeObject(preview);
        bodyPreviews.Clear();
        iconBodyWidths.Clear();
        previewFashionKeys.Clear();
    }

    private bool HasCharacterSettingsChanges(uint skinRgba)
    {
        return nowCharacterData.BodySettingsChanged ||
            (selectedModelRow != null && selectedModelRow.UInt(16) != skinRgba);
    }

    private void ExportCharacterSettingsIfChanged(string outputRoot)
    {
        if (selectedModelRow == null || selectedOriginalBodyRow == null || string.IsNullOrEmpty(bodyModelTablePath))
        {
            if (nowCharacterData.BodySettingsChanged) throw new InvalidDataException("尚未載入替換角色的身體設定。");
            return;
        }

        uint skinRgba = EncodeSkinColor(GetSelectedSkinColor());
        if (!HasCharacterSettingsChanges(skinRgba)) return;

        CfgTable.Row replacementBody = FindBodyRow(nowCharacterData.bodyType);
        if (replacementBody == null)
            throw new InvalidDataException(nowCharacterData.id + " 無法對應 Body Type " + nowCharacterData.bodyType + "。");

        CfgTable output = CfgTable.Read(File.ReadAllBytes(bodyModelTablePath), bodyModelTablePath);
        CfgTable.Row model = output.Rows.Single(row =>
            row.Name == "CHARA_MODEL_INFO" && row.Values.Length > 31 && row.UInt(0) == selectedModelRow.UInt(0));
        SetCfgInteger(model, 4, replacementBody.UInt(0));
        SetCfgInteger(model, 31, NativeBodyShape.ToNativeChestSize(nowCharacterData.chestSize));
        SetCfgInteger(model, 12, nowCharacterData.bodyDetailFlags & 0xffff);
        int hem = (nowCharacterData.bodyDetailFlags >> 16) & 0xffff;
        // Keep the target's authored uniform type. Preview outfit meshes do not select its kit.
        SetCfgInteger(model, 13, hem == 1 ? 1 : 0);
        SetCfgInteger(model, 16, unchecked((int)skinRgba));
        byte[] bytes = output.Write();
        CfgTable check = CfgTable.Read(bytes, "身體設定輸出驗證");
        CfgTable.Row restored = check.Rows.Single(row =>
            row.Name == "CHARA_MODEL_INFO" && row.Values.Length > 31 && row.UInt(0) == selectedModelRow.UInt(0));
        if (restored.UInt(4) != replacementBody.UInt(0) ||
            restored.Int(31) != NativeBodyShape.ToNativeChestSize(nowCharacterData.chestSize) ||
            restored.Int(12) != (nowCharacterData.bodyDetailFlags & 0xffff) ||
            restored.Int(13) != (hem == 1 ? 1 : 0) || restored.UInt(5) != selectedModelRow.UInt(5) ||
            restored.UInt(16) != skinRgba)
            throw new InvalidDataException("身體設定輸出驗證失敗。");

        string relative = bodyModelTablePath.Substring(DumpDataManager.CommonPath.Length + 1);
        string destination = Path.Combine(outputRoot, "common", relative);
        Directory.CreateDirectory(Path.GetDirectoryName(destination));
        File.WriteAllBytes(destination, bytes);
    }

    private static void SetCfgInteger(CfgTable.Row row, int index, long value)
    {
        if (index < 0 || index >= row.Values.Length || row.Types[index] != 1)
            throw new InvalidDataException(row.Name + " 的欄位 " + index + " 不是整數欄位。");
        row.Values[index] = value;
    }

    internal void RestoreSavedBody(CharacterMakerDocument document, List<string> warnings)
    {
        if (string.IsNullOrWhiteSpace(document.TemplateId) ||
            !string.Equals(nowCharacterData.TemplateId, document.TemplateId, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            document.UpgradeBodySettings(bodyModelTable);
            document.NormalizeBodyGender(bodyModelTable, CurrentGender, selectedOriginalBodyRow);
        }
        catch (Exception exception) { warnings.Add("舊體型設定轉換失敗：" + exception.Message); return; }
        if (document.bodyType < 1 || document.bodyType > 8 ||
            document.chestSize < -1 || document.chestSize > 2 ||
            document.uniformCollar < 0 || document.uniformCollar > 1 ||
            document.Hem < 0 || document.Hem > 2 || document.uniformSleeve < 0 || document.uniformSleeve > 1 ||
            !IsUnitColor(document.skinColor))
        {
            warnings.Add("身體設定無效，已保留目標角色的目前設定。");
            return;
        }
        int previousBodyType = nowCharacterData.bodyType;
        int previousChestSize = nowCharacterData.chestSize;
        uint previousVariant = nowCharacterData.bodyVariantId;
        string previousAvatarBody = nowCharacterData.bodyAvatarCode;
        int previousDetails = nowCharacterData.bodyDetailFlags;
        int previousSleeve = nowCharacterData.uniformSleeve;
        Color previousSkinColor = GetSelectedSkinColor();
        try
        {
            nowCharacterData.bodyType = document.bodyType;
            nowCharacterData.bodyVariantId = document.bodyVariantId;
            nowCharacterData.bodyAvatarCode = document.bodyAvatarCode;
            nowCharacterData.chestSize = document.chestSize;
            nowCharacterData.bodyDetailFlags = document.uniformCollar | (document.Hem << 16);
            nowCharacterData.uniformSleeve = document.uniformSleeve;
            NormalizeFashion();
            if (SkinColor != null) SkinColor.color = document.skinColor;
            appliedSkinColor = document.skinColor;
            hasAppliedSkinColor = true;
            ClearBodyPreviews();
            PreloadBodyPreviews();

            ShowBodyPreview(document.bodyType);
            ApplySkinColorToBodyPreviews(document.skinColor);
        }
        catch (Exception exception)
        {
            nowCharacterData.bodyType = previousBodyType;
            nowCharacterData.bodyVariantId = previousVariant;
            nowCharacterData.bodyAvatarCode = previousAvatarBody;
            nowCharacterData.chestSize = previousChestSize;
            nowCharacterData.bodyDetailFlags = previousDetails;
            nowCharacterData.uniformSleeve = previousSleeve;
            if (SkinColor != null) SkinColor.color = previousSkinColor;
            appliedSkinColor = previousSkinColor;
            try
            {
                ClearBodyPreviews();
                PreloadBodyPreviews();

                ShowBodyPreview(previousBodyType);
                ApplySkinColorToBodyPreviews(previousSkinColor);
            }
            catch (Exception rollbackException)
            {
                Debug.LogException(rollbackException);
            }
            warnings.Add("身體設定：" + exception.Message);
        }
    }

    private static bool IsUnitColor(Color color)
    {
        return float.IsFinite(color.r) && float.IsFinite(color.g) &&
            float.IsFinite(color.b) && float.IsFinite(color.a) &&
            color.r >= 0f && color.r <= 1f && color.g >= 0f && color.g <= 1f &&
            color.b >= 0f && color.b <= 1f && color.a >= 0f && color.a <= 1f;
    }

    public void OnClickAvatarInfo()
    {
        manager.RefreshPageAvailability();
        if (!manager.HasAvatarInfo()) return;
        if (string.IsNullOrWhiteSpace(nowCharacterData.TemplateId) || bodyParent == null) return;
        try
        {
            if (bodyModelTable == null) LoadBodyTables();
            var recipe = AvatarRecipeParser.Parse(avatarSetting.nowAvatarCode);
            var settings = AvatarRecipeParser.ResolveBody(recipe, bodyModelTable);
            if (Identity != null)
            {
                var body = bodyModelTable.Rows.First(r => NativeBodyTypes.IsBody(r) && r.UInt(0) == settings.VariantId);
                Identity.gender = body.Int(5) >= 4 ? 2 : 1;
                RefreshIdentityFields();
            }
            var document = new CharacterMakerDocument { version = 15, targetId = nowCharacterData.TemplateId,
                bodyType = settings.BodyType, bodyVariantId = settings.VariantId,
                bodyAvatarCode = settings.Code, chestSize = settings.ChestSize,
                uniformCollar = settings.Collar, uniformSleeve = settings.Sleeve, uniformHem = settings.Hem,
                equipmentFlag2 = settings.Hem == 1 ? 1 : 0, skinColor = GetSelectedSkinColor() };
            var warnings = new List<string>();
            RestoreSavedBody(document, warnings);
            RefreshBodySettingButtons();
            if (warnings.Count > 0) Debug.LogWarning(string.Join("\n", warnings));
        }
        catch (Exception exception) { Debug.LogException(exception); }
    }

}
