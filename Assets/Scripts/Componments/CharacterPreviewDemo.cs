using System;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.UI;
using UnityEngine.SceneManagement;
using UnityEngine.UI;
using G4Runtime;

public sealed class CharacterPreviewDemo : MonoBehaviour
{
    public Dropdown CharacterDropdown { get; private set; }
    public string Status => status != null ? status.text : "";
    public GameObject CurrentHead => current;
    Text status;
    Camera previewCamera;
    Transform pivot;
    GameObject current;
    HeadModelLoader loader;
    IReadOnlyList<HeadEntry> heads;
    CancellationTokenSource lifetime, loading;
    Font font;
    float radius = 1, distance = 3, yaw, pitch;
    bool dragging;
    int generation;
    G4Composition.CompositionEditor composition;
    bool compositionMode;
    public void SetCompositionMode(bool value)
    {
        compositionMode = value;
        pivot.gameObject.SetActive(!value);
        previewCamera.gameObject.SetActive(!value);
        transform.Find("Preview UI").gameObject.SetActive(!value);
        if (!value && composition != null) composition.gameObject.SetActive(false);
        if (value)
        {
            if (composition == null)
            {
                var go = new GameObject("Head composition"); go.transform.SetParent(transform, false);
                composition = go.AddComponent<G4Composition.CompositionEditor>();
                composition.Initialize(this, heads, font, CharacterDropdown);
            }
            else composition.Resume();
        }
    }

    async void Start()
    {
        lifetime = new CancellationTokenSource();
        if (!DumpDataManager.ValidateSavedPath())
        {
            _ = SceneManager.LoadSceneAsync("StartScene");
            return;
        }
        try
        {
            BuildPreview();
            loader = new HeadModelLoader();
            status.text = Tr("Scanning heads...", "正在讀取頭部清單…");
            heads = await loader.GetHeadsAsync(lifetime.Token);
            if (this == null || lifetime.IsCancellationRequested) return;
            var options = new List<Dropdown.OptionData>();
            foreach (var head in heads) options.Add(new Dropdown.OptionData(head.DisplayName));
            CharacterDropdown.ClearOptions(); CharacterDropdown.AddOptions(options);
            CharacterDropdown.interactable = heads.Count > 0;
            CharacterDropdown.onValueChanged.AddListener(SelectHead);
            if (heads.Count > 0) SelectHead(0);
            else status.text = Tr("No G4MD/G4MG pairs in common/chr/_face.", "common/chr/_face 中沒有成對的 G4MD／G4MG。");
        }
        catch (OperationCanceledException) { }
        catch (Exception e) { if (this != null && status != null) status.text = e.Message; Debug.LogException(e); }
    }
    async void SelectHead(int index)
    {
        if (heads == null || index < 0 || index >= heads.Count) return;
        loading?.Cancel(); loading?.Dispose();
        loading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = loading.Token; int request = ++generation;
        status.text = Tr("Loading: ", "載入中：") + heads[index].DisplayName;
        try
        {
            var result = await loader.LoadHeadAsync(heads[index], token);
            if (this == null || token.IsCancellationRequested || request != generation)
            {
                Destroy(result.Root); return;
            }
            if (current != null) { current.SetActive(false); Destroy(current); }
            current = result.Root;
            current.transform.SetParent(pivot, false);
            pivot.rotation = Quaternion.identity;
            Bounds bounds = default; bool first = true;
            foreach (var renderer in current.GetComponentsInChildren<Renderer>())
            {
                if (first) { bounds = renderer.bounds; first = false; } else bounds.Encapsulate(renderer.bounds);
            }
            current.transform.position -= bounds.center;
            radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
            ResetView();
            status.text = heads[index].DisplayName + (result.Warnings.Count == 0 ? "" : "\n" + string.Join("\n", result.Warnings));
            foreach (string warning in result.Warnings) Debug.LogWarning(warning);
        }
        catch (OperationCanceledException) { }
        catch (Exception e)
        {
            if (this != null && request == generation) status.text = Tr("Load failed: ", "載入失敗：") + e.Message;
            Debug.LogWarning(e.ToString());
        }
    }
    string Tr(string en, string zh) => SettingManager.Settings["language"].StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? zh : en;
    public void ResetView()
    {
        yaw = pitch = 0;
        float halfFov = previewCamera.fieldOfView * Mathf.Deg2Rad * 0.5f;
        float horizontal = Mathf.Atan(Mathf.Tan(halfFov) * previewCamera.aspect);
        distance = radius / Mathf.Sin(Mathf.Min(halfFov, horizontal)) * 1.15f;
        ApplyView();
    }
    void Update()
    {
        if (compositionMode || current == null || Mouse.current == null) return;
        var mouse = Mouse.current;
        bool overUI = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject();
        if (mouse.leftButton.wasPressedThisFrame) dragging = !overUI;
        if (!mouse.leftButton.isPressed) dragging = false;
        if (dragging && !overUI)
        {
            Vector2 delta = mouse.delta.ReadValue();
            yaw += delta.x * 0.3f; pitch = Mathf.Clamp(pitch - delta.y * 0.3f, -85, 85);
        }
        if (!overUI)
            distance = Mathf.Clamp(distance * Mathf.Exp(-mouse.scroll.ReadValue().y * 0.001f), radius * 1.15f, radius * 20);
        ApplyView();
    }
    void ApplyView()
    {
        pivot.rotation = Quaternion.Euler(pitch, yaw, 0);
        previewCamera.transform.position = new Vector3(0, 0, -distance);
        previewCamera.transform.rotation = Quaternion.identity;
        previewCamera.nearClipPlane = Mathf.Max(radius * 0.005f, 0.0001f);
        previewCamera.farClipPlane = Mathf.Max(distance + radius * 4, 10);
    }
    void BuildPreview()
    {
        font = Font.CreateDynamicFontFromOSFont(new[] { "Microsoft JhengHei", "Arial" }, 18);
        if (font == null) font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");
        pivot = new GameObject("Head pivot").transform; pivot.SetParent(transform, false);
        previewCamera = new GameObject("Preview Camera", typeof(Camera), typeof(AudioListener)).GetComponent<Camera>();
        previewCamera.transform.SetParent(transform, false);
        previewCamera.clearFlags = CameraClearFlags.SolidColor; previewCamera.backgroundColor = new Color(0.105f, 0.12f, 0.16f);
        previewCamera.fieldOfView = 35;
        if (EventSystem.current == null)
        {
            var events = new GameObject("EventSystem", typeof(EventSystem), typeof(InputSystemUIInputModule));
            events.transform.SetParent(transform, false);
        }
        var canvasObject = new GameObject("Preview UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
        canvasObject.transform.SetParent(transform, false);
        canvasObject.GetComponent<Canvas>().renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasObject.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1280, 720);
        var bar = Rect("Controls", canvasObject.transform); SetRect(bar, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -100), Vector2.zero);
        bar.gameObject.AddComponent<Image>().color = new Color(0.04f, 0.05f, 0.07f, 0.95f);
        Text title = Label("Title", bar, Tr("Head preview", "頭部預覽"), 22); SetRect(title.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(24, -38), new Vector2(-24, -8));
        CharacterDropdown = BuildDropdown(bar);
        var composeRect = Rect("Compose", bar);
        SetRect(composeRect, new Vector2(1, 1), Vector2.one, new Vector2(-220, -38), new Vector2(-24, -6));
        composeRect.gameObject.AddComponent<Image>().color = new Color(0.22f, 0.3f, 0.4f);
        var composeButton = composeRect.gameObject.AddComponent<Button>();
        composeButton.onClick.AddListener(() => { if (heads != null) SetCompositionMode(true); });
        Label("Label", composeRect, "部件拼貼 / Compose", 16).alignment = TextAnchor.MiddleCenter;
        var buttonRect = Rect("Reset view", bar); SetRect(buttonRect, new Vector2(1, 0), new Vector2(1, 0), new Vector2(-180, 12), new Vector2(-24, 52));
        buttonRect.gameObject.AddComponent<Image>().color = new Color(0.22f, 0.3f, 0.4f);
        var button = buttonRect.gameObject.AddComponent<Button>(); button.targetGraphic = buttonRect.GetComponent<Image>(); button.onClick.AddListener(ResetView);
        Label("Label", buttonRect, Tr("Reset view", "重設視角"), 18).alignment = TextAnchor.MiddleCenter;
        status = Label("Status", canvasObject.transform, "", 16);
        SetRect(status.rectTransform, Vector2.zero, new Vector2(1, 0), new Vector2(24, 20), new Vector2(-24, 150));
        status.alignment = TextAnchor.LowerLeft;
        var help = Label("Help", canvasObject.transform, Tr("Drag to rotate  ·  Scroll to zoom", "拖曳旋轉  ·  滾輪縮放"), 16);
        SetRect(help.rectTransform, new Vector2(0, 1), new Vector2(1, 1), new Vector2(24, -132), new Vector2(-24, -106));
    }
    Dropdown BuildDropdown(Transform parent)
    {
        var rect = Rect("Character", parent); SetRect(rect, Vector2.zero, new Vector2(1, 0), new Vector2(24, 12), new Vector2(-196, 52));
        var image = rect.gameObject.AddComponent<Image>(); image.color = new Color(0.2f, 0.25f, 0.32f);
        var dropdown = rect.gameObject.AddComponent<Dropdown>(); dropdown.targetGraphic = image; dropdown.interactable = false;
        var caption = Label("Caption", rect, Tr("Loading...", "載入中…"), 18); caption.alignment = TextAnchor.MiddleLeft;
        caption.rectTransform.offsetMin = new Vector2(12, 0); caption.rectTransform.offsetMax = new Vector2(-40, 0);
        var arrow = Label("Arrow", rect, "▼", 16); arrow.alignment = TextAnchor.MiddleRight; arrow.rectTransform.offsetMax = new Vector2(-12, 0);
        var template = Rect("Template", rect); template.pivot = new Vector2(0.5f, 1); SetRect(template, Vector2.zero, new Vector2(1, 0), new Vector2(0, -300), Vector2.zero);
        template.gameObject.AddComponent<Image>().color = new Color(0.12f, 0.16f, 0.22f);
        var scroll = template.gameObject.AddComponent<ScrollRect>(); scroll.horizontal = false; scroll.movementType = ScrollRect.MovementType.Clamped;
        var viewport = Rect("Viewport", template); Stretch(viewport); viewport.offsetMax = new Vector2(-20, 0); viewport.gameObject.AddComponent<RectMask2D>();
        var content = Rect("Content", viewport); content.pivot = new Vector2(0.5f, 1); SetRect(content, new Vector2(0, 1), new Vector2(1, 1), new Vector2(0, -36), Vector2.zero);
        var item = Rect("Item", content); SetRect(item, new Vector2(0, 0.5f), new Vector2(1, 0.5f), new Vector2(0, -18), new Vector2(0, 18));
        var background = item.gameObject.AddComponent<Image>(); background.color = new Color(0.2f, 0.28f, 0.38f);
        var toggle = item.gameObject.AddComponent<Toggle>(); toggle.targetGraphic = background;
        var check = Label("Checkmark", item, "•", 18); SetRect(check.rectTransform, Vector2.zero, new Vector2(0, 1), Vector2.zero, new Vector2(24, 0)); check.alignment = TextAnchor.MiddleCenter; toggle.graphic = check;
        var label = Label("Item label", item, "Head", 17); label.alignment = TextAnchor.MiddleLeft; label.rectTransform.offsetMin = new Vector2(28, 0); label.rectTransform.offsetMax = new Vector2(-8, 0);
        scroll.viewport = viewport; scroll.content = content; scroll.scrollSensitivity = 24;
        var scrollBarRect = Rect("Scrollbar", template);
        SetRect(scrollBarRect, new Vector2(1, 0), Vector2.one, new Vector2(-18, 2), new Vector2(-2, -2));
        scrollBarRect.gameObject.AddComponent<Image>().color = new Color(0.07f, 0.09f, 0.13f);
        var scrollbar = scrollBarRect.gameObject.AddComponent<Scrollbar>(); scrollbar.direction = Scrollbar.Direction.BottomToTop;
        var handle = Rect("Handle", scrollBarRect); Stretch(handle);
        var handleImage = handle.gameObject.AddComponent<Image>(); handleImage.color = new Color(0.48f, 0.57f, 0.7f);
        scrollbar.handleRect = handle; scrollbar.targetGraphic = handleImage;
        scroll.verticalScrollbar = scrollbar;
        dropdown.template = template; dropdown.captionText = caption; dropdown.itemText = label;
        template.gameObject.SetActive(false);
        return dropdown;
    }
    static RectTransform Rect(string name, Transform parent)
    {
        var rect = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); rect.SetParent(parent, false); return rect;
    }
    Text Label(string name, Transform parent, string text, int size)
    {
        var rect = Rect(name, parent); Stretch(rect);
        var label = rect.gameObject.AddComponent<Text>(); label.font = font; label.fontSize = size; label.color = Color.white; label.text = text; label.raycastTarget = false;
        return label;
    }
    static void Stretch(RectTransform r) => SetRect(r, Vector2.zero, Vector2.one, Vector2.zero, Vector2.zero);
    static void SetRect(RectTransform r, Vector2 min, Vector2 max, Vector2 offsetMin, Vector2 offsetMax) { r.anchorMin = min; r.anchorMax = max; r.offsetMin = offsetMin; r.offsetMax = offsetMax; }
    void OnDestroy()
    {
        ++generation; loading?.Cancel(); loading?.Dispose(); lifetime?.Cancel(); lifetime?.Dispose();
        if (font != null && font.name != "LegacyRuntime") Destroy(font);
    }
}
