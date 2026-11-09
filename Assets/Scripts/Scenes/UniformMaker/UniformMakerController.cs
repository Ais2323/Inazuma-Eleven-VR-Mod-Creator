using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using G4Composition;
using G4Runtime;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController : MonoBehaviour
{
    public RectTransform content;
    public Button itemTemplate;
    public Button mainButton, subButton, normalButton, keeperButton;
    public Button addButton;
    public InputField uniformNameInput;
    public Toggle freeShopToggle;
    bool isNewUniform;
    string newUniformKey;
    public PreviewCameraController preview;
    public Transform modelParent;
    public Slider numberSlider;
    [Range(0, 99)] public int uniformNumber = 10;

    UniformCatalog catalog;
    readonly List<Button> items = new();
    readonly Dictionary<int, NativeUniformFashion.Selection> selections = new();
    GameObject model;
    int selected = -1, side, role;
    bool framed;
    string locale;
    int appliedNumber;

    IEnumerator Start()
    {
        if (numberSlider != null)
        {
            numberSlider.SetValueWithoutNotify(Mathf.Clamp(uniformNumber, 0, 99));
            numberSlider.onValueChanged.AddListener(value =>
            {
                uniformNumber = Mathf.Clamp(Mathf.RoundToInt(value), 0, 99);
                ApplyNumber();
            });
        }
        InitializePartControls();
        InitializeFbxControls();
        InitializePersistence();
        InitializeInfo();
        InitializePortraitControls();
        InitializeBodySettings();
        if (addButton != null) { addButton.interactable = false; addButton.onClick.AddListener(OnClickAdd); }
        mainButton.onClick.AddListener(() => SelectVariant(0, role));
        subButton.onClick.AddListener(() => SelectVariant(1, role));
        normalButton.onClick.AddListener(() => SelectVariant(side, 0));
        keeperButton.onClick.AddListener(() => SelectVariant(side, 1));
        RefreshButtons();
        if (!DumpDataManager.ValidateSavedPath())
        {
            Debug.LogError("[UniformMaker] " + DumpDataManager.LastError);
            yield break;
        }
        try { catalog = new UniformCatalog(); }
        catch (Exception error) { Debug.LogException(error, this); yield break; }
        locale = UniformCatalog.Locale;
        for (int i = 0; i < catalog.Entries.Count; i++)
        {
            int index = i;
            // Clone the button itself: Temp is only the scene's inactive holder.
            Button button = Instantiate(itemTemplate, content, false);
            button.name = catalog.Entries[i].Key;
            button.gameObject.SetActive(true);
            button.GetComponentInChildren<Text>(true).text = catalog.Name(catalog.Entries[i], locale);
            button.onClick.AddListener(() => SelectUniform(index));
            items.Add(button);
            if (i % 24 == 23) yield return null;
        }
        if (items.Count > 0) SelectUniform(0);
        if (saveButton != null) saveButton.interactable = selected >= 0;
        if (loadButton != null) loadButton.interactable = true;
        if (addButton != null) addButton.interactable = true;
    }

    void Update()
    {
        UpdateSkinColor();
        if (model != null && appliedNumber != Mathf.Clamp(uniformNumber, 0, 99)) ApplyNumber();
        if (catalog == null || locale == UniformCatalog.Locale) return;
        locale = UniformCatalog.Locale;
        for (int i = 0; i < items.Count; i++)
            items[i].GetComponentInChildren<Text>(true).text = catalog.Name(catalog.Entries[i], locale);
    }

    public void SelectUniform(int index)
    {
        if (catalog == null || index < 0 || index >= catalog.Entries.Count) return;
        CloseFbxBindings();
        DisposePartEditor();
        isNewUniform = false; newUniformKey = null; shareAwayModels = false;
        if (freeShopToggle != null) freeShopToggle.SetIsOnWithoutNotify(false);
        selected = index;
        LoadUniformInfo();
        CloseModelSelect();
        selections.Clear();
        NormalizeFashion();
        if (GetSelection(side, role) == null)
        {
            side = 0; role = 0;
        }
        LoadPreview();
        RefreshButtons();
    }

    public void OnClickAdd()
    {
        if (catalog == null || selected < 0 || isNewUniform) return;
        CloseFbxBindings();
        CloseModelSelect();
        isNewUniform = true;
        newUniformKey = Guid.NewGuid().ToString("N");
        RefreshButtons();
    }

    void SelectVariant(int newSide, int newRole)
    {
        if (selected < 0 || GetSelection(newSide, newRole) == null) return;
        side = newSide; role = newRole;
        selections.Clear();
        NormalizeFashion();
        LoadPreview();
        RefreshButtons();
    }

    NativeUniformFashion.Selection GetSelection(int wantedSide, int wantedRole)
    {
        if (selected < 0) return null;
        int key = wantedSide * 2 + wantedRole;
        if (!selections.TryGetValue(key, out var selection))
        {
            try
            {
                int modelSide = shareAwayModels && wantedSide == 1 ? 0 : wantedSide;
                var edit = EditFor(modelSide, wantedRole, false);
                uint custom = edit?.Clothes ?? 0;
                if (shareAwayModels && wantedSide == 1 && custom == 0) custom = catalog.Entries[selected].Models[0, wantedRole];
                selection = catalog.Resolve(catalog.Entries[selected], wantedSide, wantedRole, CurrentBody, sleeve, hem, custom)
                    ?? catalog.Resolve(catalog.Entries[selected], wantedSide, wantedRole, CurrentBody, overrideId: custom);
            }
            catch (Exception error) { Debug.LogWarning("[UniformMaker] " + catalog.Entries[selected].Key + ": " + error.Message, this); }
            selections[key] = selection;
        }
        return selection;
    }

    void RefreshButtons()
    {
        mainButton.interactable = side != 0 && GetSelection(0, role) != null;
        subButton.interactable = side != 1 && GetSelection(1, role) != null;
        normalButton.interactable = role != 0 && GetSelection(side, 0) != null;
        keeperButton.interactable = role != 1 && GetSelection(side, 1) != null;
        for (int i = 0; i < items.Count; i++) items[i].interactable = isNewUniform || i != selected;
        if (uniformNameInput != null) uniformNameInput.interactable = selected >= 0;
        if (freeShopToggle != null) freeShopToggle.interactable = selected >= 0;
        if (addButton != null) addButton.interactable = catalog != null && !isNewUniform;
        RefreshBodySettingButtons();
        RefreshPartControls();
        RefreshPortraitIcon();
    }

    void LoadPreview()
    {
        ClearPreview();
        var outfit = GetSelection(side, role);
        if (outfit == null) return;
        model = new GameObject(catalog.Entries[selected].Key);
        model.transform.SetParent(modelParent, false);
        try
        {
            var rig = NativeBodyShape.Skeleton(CurrentBody);
            var pose = NativeBodyShape.Outfit(CurrentBody, chestSize, hem);
            GameObject Load(string mesh, string texture, UniformPartKind kind = UniformPartKind.Upper)
            {
                UniformFbxWriter.Result imported = BuildPartFbx(mesh, kind);
                var part = G4ModelLoader.CreateModel(Path.Combine("chr", Path.GetDirectoryName(mesh)),
                    Path.GetFileNameWithoutExtension(mesh), Path.GetFileNameWithoutExtension(texture),
                    Path.Combine("chr", Path.GetDirectoryName(texture)), boneTransforms: pose,
                    skeletonOverride: rig, headLocalSkinning: false, metadataOverride: imported?.Metadata, geometryOverride: imported?.Geometry);
                part.transform.SetParent(model.transform, false);
                return part;
            }
            var uniform = PartHidden(UniformPartKind.Upper) && PartHidden(UniformPartKind.Lower) ? null : Load(outfit.Uniform, outfit.UniformTexture);
            uniformModel = uniform;
            foreach (var renderer in uniform != null ? uniform.GetComponentsInChildren<Renderer>() : Array.Empty<Renderer>())
                renderer.sharedMaterial.SetColor("_SkinColor", SelectedSkinColor);
            var skinOutfit = ResolveAccessory(UniformPartKind.Skin, outfit);
            if (!string.IsNullOrWhiteSpace(skinOutfit?.Skin))
            {
                var skin = Load(skinOutfit.Skin, skinOutfit.SkinTexture, UniformPartKind.Skin); skinModel = skin;
                foreach (var renderer in skin.GetComponentsInChildren<Renderer>())
                {
                    renderer.sharedMaterial.SetFloat("_WholeSkin", 1);
                    renderer.sharedMaterial.SetColor("_SkinColor", SelectedSkinColor);
                }
            }
            foreach (var filter in uniform != null ? uniform.GetComponentsInChildren<MeshFilter>() : Array.Empty<MeshFilter>())
            {
                if (ModelEdit?.Fbx == null && !ModelEdit.PartFbx.ContainsKey(UniformPartKind.Upper)) {
                    if (filter.name.Equals("collar_10", StringComparison.OrdinalIgnoreCase)) filter.gameObject.SetActive(collar == 0);
                    if (filter.name.Equals("collar_11", StringComparison.OrdinalIgnoreCase)) filter.gameObject.SetActive(collar == 1);
                }
            }
            var numberOutfit = ResolveAccessory(UniformPartKind.Number, outfit);
            if (!string.IsNullOrWhiteSpace(numberOutfit?.Number) && !string.IsNullOrWhiteSpace(numberOutfit.NumberTexture))
            {
                numberModel = Load(numberOutfit.Number, numberOutfit.NumberTexture, UniformPartKind.Number);
                FilterLabels(numberModel, false);
                ApplyNumber();
            }
            var nameOutfit = ResolveAccessory(UniformPartKind.Name, outfit);
            if (!string.IsNullOrWhiteSpace(nameOutfit?.Number) && UniformLabelAsset.HasPart(nameOutfit.Number, true)) {
                nameModel = Load(nameOutfit.Number, nameOutfit.NumberTexture, UniformPartKind.Name); FilterLabels(nameModel, true);
            }
            var armband = ResolveArmband(outfit);
            if (!string.IsNullOrWhiteSpace(armband?.Armband) && !string.IsNullOrWhiteSpace(armband.ArmbandTexture))
            {
                armbandModel = Load(armband.Armband, armband.ArmbandTexture, UniformPartKind.Armband);
                foreach (var renderer in armbandModel.GetComponentsInChildren<Renderer>())
                    renderer.sharedMaterial.SetColor("_SkinColor", SelectedSkinColor);
            }
            if (role == 1 && !PartHidden(UniformPartKind.Gloves))
            {
                var gloves = ModelEdit?.Gloves > 0 ? catalog.ResolveGloves(ModelEdit.Gloves, CurrentBody) :
                    catalog.ResolveGloves(catalog.Entries[selected], shareAwayModels && side == 1 ? 0 : side, CurrentBody);
                if (gloves != null)
                {
                    gloveModel = Load(gloves.Model, gloves.Texture, UniformPartKind.Gloves);
                    foreach (var renderer in gloveModel.GetComponentsInChildren<Renderer>())
                        renderer.sharedMaterial.SetColor("_SkinColor", SelectedSkinColor);
                }
            }
            ApplyPartTextures();
            var renderers = model.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0) return;
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers) bounds.Encapsulate(renderer.bounds);
            Vector3 neck = new Vector3(bounds.center.x, bounds.max.y, bounds.center.z);
            int bone = Array.FindIndex(rig.Names, name => name == "c_neck_1_0");
            if (bone >= 0)
            {
                Vector3 point = rig.Bind[bone].MultiplyPoint3x4(Vector3.zero);
                neck = model.transform.TransformPoint(new Vector3(point.x, point.y, -point.z));
            }
            preview.fallbackCenter = neck;
            if (!framed) { preview.FrameBounds(bounds); framed = true; }
        }
        catch (Exception error)
        {
            ClearPreview();
            Debug.LogError("[UniformMaker] " + catalog.Entries[selected].Key + " / " + outfit.Uniform + ": " + error, this);
        }
    }

    void ClearPreview()
    {
        numberModel = null;
        uniformModel = armbandModel = gloveModel = skinModel = nameModel = null;
        ClearPartImages();
        if (model == null) return;
        model.SetActive(false);
        foreach (var resources in model.GetComponentsInChildren<RuntimeHeadResources>(true)) resources.Release();
        if (Application.isPlaying) Destroy(model); else DestroyImmediate(model);
        model = null;
    }

    void OnDestroy() { CloseFbxBindings(); infoTranslation?.Cancel(); ReleaseInfoIcon(); ReleasePortraitIcon(); ClearPreview(); DisposePartEditor(); }

    GameObject numberModel;
    void ApplyNumber()
    {
        appliedNumber = Mathf.Clamp(uniformNumber, 0, 99);
        if (numberSlider != null) numberSlider.SetValueWithoutNotify(appliedNumber);
        if (numberModel == null) return;
        // Native atlases contain 0-99 in ten columns, starting at UV (0,0).
        // The mesh already occupies one cell and carries the native orientation.
        var offset = new Vector2(appliedNumber % 10 * .1f, appliedNumber / 10 * .1f);
        foreach (var renderer in numberModel.GetComponentsInChildren<Renderer>())
        {
            renderer.sharedMaterial.mainTextureOffset = offset;
        }
    }
}
