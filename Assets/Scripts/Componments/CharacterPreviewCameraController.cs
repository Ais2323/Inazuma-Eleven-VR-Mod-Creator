using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

[RequireComponent(typeof(RawImage))]
public sealed class CharacterPreviewCameraController : MonoBehaviour, IPointerDownHandler, IPointerUpHandler, IDragHandler, IScrollHandler
{
    public Camera previewCamera;
    public CharacterManager characterManager;
    public Vector3 fallbackCenter = new Vector3(0, 1.25f, 0);
    public float rotationSpeed = 180f;
    public float zoomSpeed = 0.18f;
    public float minimumDistance = 0.25f;
    public float maximumDistance = 8f;

    RawImage display;
    RectTransform view;
    Canvas canvas;
    RenderTexture texture, originalTarget;
    Rect originalRect;
    float originalAspect;
    bool originalEnabled, capturedCamera, initialized, dragging;
    float yaw, pitch, distance;
    readonly Vector3[] corners = new Vector3[4];

    Vector3 Center => characterManager != null && characterManager.TryGetPreviewNeck(out var point) ? point : fallbackCenter;
    Camera EventCamera => canvas != null && canvas.renderMode != RenderMode.ScreenSpaceOverlay ? canvas.worldCamera : null;

    void OnEnable()
    {
        display = GetComponent<RawImage>();
        view = (RectTransform)transform;
        canvas = GetComponentInParent<Canvas>();
        display.raycastTarget = true;
    }

    void LateUpdate()
    {
        if (previewCamera == null) return;
        if (!capturedCamera)
        {
            originalTarget = previewCamera.targetTexture;
            originalRect = previewCamera.rect;
            originalAspect = previewCamera.aspect;
            originalEnabled = previewCamera.enabled;
            capturedCamera = true;
        }
        UpdateTexture();
        if (texture == null) return;
        if (!initialized)
        {
            Vector3 offset = previewCamera.transform.position - Center;
            distance = Mathf.Clamp(offset.magnitude, minimumDistance, maximumDistance);
            yaw = Mathf.Atan2(-offset.x, -offset.z) * Mathf.Rad2Deg;
            pitch = Mathf.Clamp(Mathf.Asin(Mathf.Clamp(offset.normalized.y, -1, 1)) * Mathf.Rad2Deg, -75, 75);
            initialized = true;
        }
        ApplyCamera();
    }

    void UpdateTexture()
    {
        view.GetWorldCorners(corners);
        Vector2 low = RectTransformUtility.WorldToScreenPoint(EventCamera, corners[0]);
        Vector2 high = RectTransformUtility.WorldToScreenPoint(EventCamera, corners[2]);
        float pixelsWide = Mathf.Max(16, Mathf.Abs(high.x - low.x));
        float pixelsHigh = Mathf.Max(16, Mathf.Abs(high.y - low.y));
        float resolutionScale = Mathf.Min(1f, 2048f / Mathf.Max(pixelsWide, pixelsHigh));
        int width = Mathf.Max(1, Mathf.RoundToInt(pixelsWide * resolutionScale));
        int height = Mathf.Max(1, Mathf.RoundToInt(pixelsHigh * resolutionScale));
        if (texture == null || texture.width != width || texture.height != height)
        {
            ReleaseTexture();
            texture = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32) {
                name = "Character View Preview", filterMode = FilterMode.Bilinear };
            texture.Create();
        }
        previewCamera.targetTexture = texture;
        previewCamera.rect = new Rect(0, 0, 1, 1);
        previewCamera.aspect = (float)width / height;
        previewCamera.enabled = true;
        display.texture = texture;
    }

    void ApplyCamera()
    {
        Quaternion rotation = Quaternion.Euler(pitch, yaw, 0);
        previewCamera.transform.SetPositionAndRotation(Center + rotation * Vector3.back * distance, rotation);
    }

    bool Contains(PointerEventData data) => isActiveAndEnabled &&
        RectTransformUtility.RectangleContainsScreenPoint(view, data.position, data.pressEventCamera ?? EventCamera);

    public void OnPointerDown(PointerEventData data)
    {
        if (data.button == PointerEventData.InputButton.Left && Contains(data)) dragging = true;
    }

    public void OnPointerUp(PointerEventData data)
    {
        if (data.button == PointerEventData.InputButton.Left) dragging = false;
    }

    public void OnDrag(PointerEventData data)
    {
        if (!initialized || !dragging || data.button != PointerEventData.InputButton.Left || !Contains(data)) return;
        Vector2 size = view.rect.size;
        float scale = canvas != null ? canvas.scaleFactor : 1f;
        yaw += data.delta.x / Mathf.Max(1, size.x * scale) * rotationSpeed;
        pitch = Mathf.Clamp(pitch - data.delta.y / Mathf.Max(1, size.y * scale) * rotationSpeed, -75, 75);
        ApplyCamera();
    }

    public void OnScroll(PointerEventData data)
    {
        if (!initialized || !Contains(data)) return;
        float factor = Mathf.Exp(-data.scrollDelta.y * zoomSpeed);
        distance = Mathf.Clamp(distance * factor, minimumDistance, maximumDistance);
        if (previewCamera.orthographic)
            previewCamera.orthographicSize = Mathf.Clamp(previewCamera.orthographicSize * factor, minimumDistance, maximumDistance);
        ApplyCamera();
    }

    void ReleaseTexture()
    {
        if (texture == null) return;
        if (previewCamera != null && previewCamera.targetTexture == texture) previewCamera.targetTexture = originalTarget;
        if (display != null && display.texture == texture) display.texture = null;
        texture.Release();
        Destroy(texture);
        texture = null;
    }

    void OnDisable()
    {
        dragging = false;
        if (capturedCamera && previewCamera != null) previewCamera.enabled = false;
        ReleaseTexture();
    }

    void OnDestroy()
    {
        if (!capturedCamera || previewCamera == null) return;
        previewCamera.targetTexture = originalTarget;
        previewCamera.rect = originalRect;
        previewCamera.aspect = originalAspect;
        previewCamera.enabled = originalEnabled;
    }
}
