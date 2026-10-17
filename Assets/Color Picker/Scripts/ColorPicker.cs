using System.Collections;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

public class ColorPicker : MonoBehaviour
{
    public Image BindImage;

    [Header("Color Settings")]
    [SerializeField] private Color currentColor = Color.white;
    public Color CurrentColor => currentColor;
    public float R => currentColor.r;
    public float G => currentColor.g;
    public float B => currentColor.b;
    public float A => currentColor.a;
    private float hue = 1f;
    public float Hue => hue;
    private float saturation = 1f;
    public float Saturation => saturation;
    private float brightness = 1f;
    public float Brightness => brightness;
    [SerializeField] private string hex;
    public string Hex => hex;

    [Header("Performance Settings")]
    [SerializeField, Tooltip("When on, slider colors accurately reflect how the color would change across the slider")]
    private bool adjustRGBSliderColors = true;
    [SerializeField, Range(0.1f, 1f), Tooltip("Reduces the size of rgb slider textures to improve performance")]
    private float textureSizeFactor = 0.35f;
    [Header("Canvas References")]
    [SerializeField] private Canvas canvas;
    [SerializeField] private GraphicRaycaster raycaster;

    [Header("Color Display References")]
    [SerializeField] private Image currentColorImg;
    [SerializeField] private Image prevColorImg;
    [Header("Hue References")]
    [SerializeField] private Slider hSlider;
    [SerializeField] private RectTransform hImg;
    [SerializeField] private SliderMode hSliderMode = SliderMode.Vertical;
    [Header("Saturation/Value References")]
    [SerializeField] private RectTransform svBox;
    [SerializeField] private Image svColorImg;
    [SerializeField] private Image saturationImg;
    [SerializeField] private Image brightnessImg;
    [SerializeField] private RectTransform svHandle;
    bool isDraggingSV = false;
    [Header("RGBA References")]
    [SerializeField] private Slider rSlider;
    [SerializeField] private RectTransform rBGTransform;
    [SerializeField] private Image rBGImg;
    [SerializeField] private Slider gSlider;
    [SerializeField] private RectTransform gBGTranform;
    [SerializeField] private Image gBGImg;
    [SerializeField] private Slider bSlider;
    [SerializeField] private RectTransform bBGTransform;
    [SerializeField] private Image bBGImg;
    [SerializeField] private Slider aSlider;
    [SerializeField] private RectTransform aBGTranform;
    [SerializeField] private Image aBGImg;
    [Header("Hex References")]
    [SerializeField] private TMP_InputField hexInputField;

    public delegate void OnColorChange(Color color);
    public event OnColorChange OnColorChanged;
    
    void Start()
    {
        GenerateAllImages();
        UpdateSVHandle();
    }
    //Remove OnEnable if you want to set previous color manually
    private void OnEnable()
    {
        //Setting previous color to current color so user can see the difference
        currentColor = BindImage.color;
        SetPreviousColor(BindImage.color);
    }
    void Update()
    {
        UpdateDisplayColor();

        //If we click on sv box we want to change sv
        if (!isDraggingSV && Input.GetMouseButtonDown(0))
        {
            List<RaycastResult> results = CastRayFromMouse();
            for (int i = 0; i < results.Count; i++)
            {
                if(results[0].gameObject == svBox.gameObject)
                {
                    StartCoroutine(DragSV());
                    return;
                }
            }
        }
    }
    List<RaycastResult> CastRayFromMouse()
    {
        // Create a PointerEventData object
        PointerEventData pointerData = new PointerEventData(EventSystem.current);
        pointerData.position = Input.mousePosition;

        // Create a list to store raycast results
        List<RaycastResult> results = new List<RaycastResult>();

        // Perform the raycast
        raycaster.Raycast(pointerData, results);
        return results;
    }

    void GenerateAllImages()
    {
        GenerateHueImage();
        GenerateSVImage();
        GenerateAImage();
        UpdateRGBASliders(!adjustRGBSliderColors);
    }
    void GenerateHueImage()
    {
        int width = 1;
        int height = 1;
        Texture2D texture = null;
        switch (hSliderMode)
        {
            case SliderMode.Horizontal:
                width = (int)hImg.rect.width;
                texture = new Texture2D(width, height);
                for (int i = 0; i < width; i++)
                {
                    float hVal = (float)i / width;
                    texture.SetPixel(i, 0, Color.HSVToRGB(hVal, 1f, 1f));
                }
                break;
            case SliderMode.Vertical:
                height = (int)hImg.rect.height;
                texture = new Texture2D(width, height);
                for (int i = 0; i < height; i++)
                {
                    float hVal = (float)i / height;
                    texture.SetPixel(0, i, Color.HSVToRGB(hVal, 1f, 1f));
                }
                break;
        }
        
        texture.Apply();
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, width, height), Vector2.zero, 1);
        Image img = hImg.GetComponent<Image>();
        img.sprite = sprite;
        img.type = Image.Type.Simple;
    }
    void GenerateSVImage()
    {
        int width = (int)svBox.rect.width;
        int height = (int)svBox.rect.height;

        Texture2D satTex = new Texture2D(width, 1);
        for (int i = 0; i < width; i++)
        {
            Color color = new Color(1, 1, 1, 1f - (float)i / width);
            satTex.SetPixel(i, 0, color);
        }
        //Making filter mode point because bilinear causes weird results with small textures
        satTex.filterMode = FilterMode.Point;
        satTex.Apply();
        Sprite satSprite = Sprite.Create(satTex, new Rect(0, 0, width, 1), Vector2.zero, 1);
        saturationImg.sprite = satSprite;
        saturationImg.type = Image.Type.Simple;

        Texture2D valTex = new Texture2D(1, height);
        for (int i = 0; i < height; i++)
        {
            Color color = new Color(0, 0, 0, 1f - (float)i / height);
            valTex.SetPixel(0, i, color);
        }
        valTex.filterMode = FilterMode.Point;
        valTex.Apply();
        Sprite valSprite = Sprite.Create(valTex, new Rect(0, 0, 1, height), Vector2.zero, 1);
        brightnessImg.sprite = valSprite;
        brightnessImg.type = Image.Type.Simple;
    }
    void GenerateRImage()
    {
        if (rSlider == null) return;
        int width = (int)(rBGTransform.rect.width * textureSizeFactor);
        if(width < 1) width = 64;
        int height = 1;
        Texture2D texture = new Texture2D(width, height);
        Color color = adjustRGBSliderColors ? currentColor : Color.black;

        for (int i = 0; i < width; i++)
        {
            float sliderVal = (float)i / width;
            color.r = sliderVal;
            texture.SetPixel(i, 0, color);
        }
        //Making filter mode point because bilinear causes weird results with small textures
        texture.filterMode = FilterMode.Point;
        texture.Apply();
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, width, height), Vector2.zero, 1);
        rBGImg.sprite = sprite;
        rBGImg.type = Image.Type.Simple;
    }
    void GenerateGImage()
    {
        if (gSlider == null) return;
        int width = (int)(gBGTranform.rect.width * textureSizeFactor);
        if (width < 1) width = 64;
        int height = 1;
        Texture2D texture = new Texture2D(width, height);
        Color color = adjustRGBSliderColors ? currentColor : Color.black;

        for (int i = 0; i < width; i++)
        {
            float sliderVal = (float)i / width;
            color.g = sliderVal;
            texture.SetPixel(i, 0, color);
        }
        //Making filter mode point because bilinear causes weird results with small textures
        texture.filterMode = FilterMode.Point;
        texture.Apply();
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, width, height), Vector2.zero, 1);
        gBGImg.sprite = sprite;
        gBGImg.type = Image.Type.Simple;
    }
    void GenerateBImage()
    {
        if (bSlider == null) return;
        int width = (int)(bBGTransform.rect.width * textureSizeFactor);
        if (width < 1) width = 64;
        int height = 1;
        Texture2D texture = new Texture2D(width, height);
        Color color = adjustRGBSliderColors ? currentColor : Color.black;

        for (int i = 0; i < width; i++)
        {
            float sliderVal = (float)i / width;
            color.b = sliderVal;
            texture.SetPixel(i, 0, color);
        }
        //Making filter mode point because bilinear causes weird results with small textures
        texture.filterMode = FilterMode.Point;
        texture.Apply();
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, width, height), Vector2.zero, 1);
        bBGImg.sprite = sprite;
        bBGImg.type = Image.Type.Simple;
    }
    void GenerateAImage()
    {
        if (aSlider == null) return;
        int width = (int)aBGTranform.rect.width;
        if (width < 1) width = 128;
        int height = 1;
        Texture2D texture = new Texture2D(width, height);
        Color color = Color.white;

        for (int i = 0; i < width; i++)
        {
            float sliderVal = (float)i / width;
            color.a = sliderVal;
            texture.SetPixel(i, 0, color);
        }
        //Making filter mode point because bilinear causes weird results with small textures
        texture.filterMode = FilterMode.Point;
        texture.Apply();
        Sprite sprite = Sprite.Create(texture, new Rect(0, 0, width, height), Vector2.zero, 1);
        aBGImg.sprite = sprite;
        aBGImg.type = Image.Type.Simple;
    }

    void UpdateRGBASliders(bool forceGenerateRGBColors = false)
    {
        if (adjustRGBSliderColors || forceGenerateRGBColors)
        {
            GenerateRImage();
            GenerateGImage();
            GenerateBImage();
        }
        UpdateASliderColor();

        rSlider.value = R;
        gSlider.value = G;
        bSlider.value = B;
        aSlider.value = A;
    }
    void UpdateDisplayColor()
    {
        if (!currentColorImg) return;
        currentColorImg.color = currentColor;
        BindImage.color = currentColor;
    }
    void UpdateHueSlider()
    {
        hSlider.value = Hue;
    }
    void UpdateASliderColor()
    {
        if (aSlider == null) return;
        Color color = currentColor;
        color.a = 1f;
        aBGImg.color = color;
    }

    IEnumerator DragSV()
    {
        isDraggingSV = true;

        while (Input.GetMouseButton(0))
        {
            //Converting mouse pos to local position inside the svBox
            RectTransformUtility.ScreenPointToLocalPointInRectangle(
                svBox,
                Input.mousePosition,
                canvas.renderMode == RenderMode.ScreenSpaceOverlay ? null : canvas.worldCamera,
                out Vector2 localPoint
            );

            Rect rect = svBox.rect;

            float clampedX = Mathf.Clamp(localPoint.x, rect.xMin, rect.xMax);
            float clampedY = Mathf.Clamp(localPoint.y, rect.yMin, rect.yMax);

            saturation = Mathf.InverseLerp(rect.xMin, rect.xMax, clampedX);
            brightness = Mathf.InverseLerp(rect.yMin, rect.yMax, clampedY);

            SetColorHSV();

            OnColorChanged?.Invoke(currentColor);

            yield return null;
        }

        isDraggingSV = false;
    }
    void UpdateSVHandle()
    {
        float width = svBox.rect.width;
        float height = svBox.rect.height;
        svHandle.localPosition = new Vector2(width * (Saturation - 0.5f), height * (Brightness - 0.5f));
    }
    void UpdateSVColor()
    {
        svColorImg.color = Color.HSVToRGB(Hue, 1f, 1f);
    }
    void UpdateHex()
    {
        hex = "#" + ColorUtility.ToHtmlStringRGB(currentColor);
        if(hexInputField == null) return;
        hexInputField.text = hex;
    }
    public void SetPreviousColor(Color color)
    {
        if (!prevColorImg) return;
        prevColorImg.color = color;
    }

    public void SetColor(Color color)
    {
        currentColor = color;
        ConvertRGBToHSV();
        UpdateSVColor();
        UpdateRGBASliders();
        UpdateSVHandle();
        UpdateHueSlider();
        UpdateDisplayColor();

        OnColorChanged?.Invoke(currentColor);
    }
    void SetColorHSV()
    {
        SetColorHSV(Hue, Saturation, Brightness);
    }
    public void SetColorHSV(float h, float s, float v)
    {
        Color hsv = Color.HSVToRGB(h, s, v);
        currentColor.r = hsv.r;
        currentColor.g = hsv.g;
        currentColor.b = hsv.b;

        UpdateSVHandle();
        if (!hexInputField.isFocused) UpdateHex();
        UpdateRGBASliders();

        OnColorChanged?.Invoke(currentColor);
    }
    public void SetColorHex(string hexVal)
    {
        if (!hexInputField.isFocused) return;
        string newHex = "#";

        for (int i = 0; i < hexVal.Length && newHex.Length < 7; i++)
        {
            if (hexVal[i] == '#') continue;

            newHex += hexVal[i];
        }

        for (int i = newHex.Length; i < 7; i++)
        {
            newHex += '0';
        }

        hex = newHex;

        ColorUtility.TryParseHtmlString(newHex, out Color hexColor);
        currentColor.r = hexColor.r;
        currentColor.g = hexColor.g;
        currentColor.b = hexColor.b;

        ConvertRGBToHSV();
        UpdateSVColor();
        UpdateRGBASliders();
        UpdateSVHandle();
        UpdateHueSlider();
        UpdateDisplayColor();

        OnColorChanged?.Invoke(currentColor);
    }
    void ConvertRGBToHSV()
    {
        if (isDraggingSV) return;
        Color.RGBToHSV(currentColor, out hue, out saturation, out brightness);
    }

    public void SetHue(float val)
    {
        hue = val;
        UpdateSVColor();
        SetColorHSV();
    }
    public void SetSaturation(float val)
    {
        saturation = val;
        SetColorHSV();
    }
    public void SetBrightness(float val)
    {
        brightness = val;
        SetColorHSV();
    }
    public void SetR(float val)
    {
        currentColor.r = val;
        ConvertRGBToHSV();
        UpdateSVColor();
        UpdateSVHandle();
        UpdateHueSlider();
        UpdateRGBASliders();
        if (!hexInputField.isFocused) UpdateHex();

        OnColorChanged?.Invoke(currentColor);
    }
    public void SetG(float val)
    {
        currentColor.g = val;
        ConvertRGBToHSV();
        UpdateSVColor();
        UpdateSVHandle();
        UpdateHueSlider();
        UpdateRGBASliders();
        if (!hexInputField.isFocused) UpdateHex();

        OnColorChanged?.Invoke(currentColor);
    }
    public void SetB(float val)
    {
        currentColor.b = val;
        ConvertRGBToHSV();
        UpdateSVColor();
        UpdateSVHandle();
        UpdateHueSlider();
        UpdateRGBASliders();
        if (!hexInputField.isFocused) UpdateHex();

        OnColorChanged?.Invoke(currentColor);
    }
    public void SetAlpha(float val)
    {
        currentColor.a = val;

        OnColorChanged?.Invoke(currentColor);
    }

    public enum SliderMode
    {
        Horizontal,
        Vertical // Add Radial
    }
}
