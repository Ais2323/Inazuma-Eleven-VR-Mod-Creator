using System;
using System.Text;
using System.IO;
using G4Composition;
using G4Runtime;
using UnityEngine;

public class AvatarDemo : MonoBehaviour
{
    public string demoCode = "";
    public Transform headParent;
    [Range(0, 7)] public int previewExpression = 0;
    [Range(0, 7)] public int previewMouthExpression = 4;
    [Header("Parsed Result")]
    public AvatarRecipeData parsedData;
    [TextArea(4, 20)] public string parsedText;
    public string lastError;
    [Header("Image Import")]
    [TextArea(4, 8)] public string recognizedText;
    public string imageImportStatus;
    public bool IsReadingImage { get; private set; }
    public UnityEngine.Events.UnityEvent<string> onImageCodeRecognized = new UnityEngine.Events.UnityEvent<string>();
    private System.Threading.CancellationTokenSource imageReadCancellation;

    /// <summary>Bind this method to a UI Button's OnClick event.</summary>
    [ContextMenu("Import Recipe Image")]
    public void SelectRecipeImage()
    {
        if (IsReadingImage) return;
        string[] paths = SFB.StandaloneFileBrowser.OpenFilePanel("選擇角色編號圖片", "",
            new[] { new SFB.ExtensionFilter("圖片", "png", "jpg", "jpeg", "bmp") }, false);
        if (paths == null || paths.Length == 0) return;
        ReadRecipeImage(paths[0]);
    }

    public async void ReadRecipeImage(string path)
    {
        if (IsReadingImage) return;
        imageReadCancellation = new System.Threading.CancellationTokenSource();
        var cancellation = imageReadCancellation;
        bool codeRecognized = false;
        IsReadingImage = true;
        imageImportStatus = "正在辨識中央角色編號…";
        lastError = "";
        recognizedText = "";
        try
        {
            var result = await AvatarRecipeImageReader.ReadAsync(path, cancellation.Token);
            if (this == null || cancellation.IsCancellationRequested) return;
            recognizedText = string.Join("\n\n", result.candidates ?? new string[0]);
            if (!string.IsNullOrEmpty(result.error)) throw new InvalidDataException(result.error);
            string candidate = AvatarRecipeParser.ExtractImageCode(result.candidates);
            codeRecognized = true;
            demoCode = candidate;
            onImageCodeRecognized.Invoke(candidate);
            imageImportStatus = "角色編號已辨識，正在建立角色…";
            // Keep the recognized code even if the existing model decoder fails.
            var data = AvatarRecipeParser.Parse(candidate);
            demoCode = candidate;
            parsedData = data;
            parsedText = Format(data);
            CreateModel(data);
            imageImportStatus = "已從圖片讀取角色編號並建立角色。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (this != null)
            {
                lastError = exception.Message;
                imageImportStatus = (codeRecognized ? "角色編號已辨識，但轉換失敗：" : "辨識失敗：") + exception.Message;
                Debug.LogWarning(imageImportStatus, this);
            }
        }
        finally
        {
            cancellation.Dispose();
            if (this != null) { imageReadCancellation = null; IsReadingImage = false; }
        }
    }

    private void OnDestroy() => imageReadCancellation?.Cancel();

    private void Start()
    {
        if (!string.IsNullOrWhiteSpace(demoCode)) ParseDemoCode();
    }

    [ContextMenu("Parse Demo Code")]
    public void ParseDemoCode()
    {
        try
        {
            parsedData = AvatarRecipeParser.Parse(demoCode);
            lastError = "";
            parsedText = Format(parsedData);
            Debug.Log(parsedText, this);
            CreateModel(parsedData);
        }
        catch (Exception exception)
        {
            parsedData = null;
            parsedText = "";
            lastError = exception.Message;
            Debug.LogException(exception, this);
        }
    }

    private static string Format(AvatarRecipeData data)
    {
        var text = new StringBuilder();
        text.AppendLine("Avatar Recipe parsed result");
        text.Append("Fields: ").Append(data.Values.Count)
            .Append(", verification: ").AppendLine(data.VerificationCode);
        text.AppendLine("Index | Category | Param | Sub | Raw | Bits | Type | Num");
        foreach (AvatarRecipeValue value in data.Values)
        {
            text.Append(value.Index).Append(" | ")
                .Append(value.Category).Append(" | ")
                .Append(value.CategoryParam).Append(" | ")
                .Append(value.CategoryParamSub).Append(" | ")
                .Append(value.RawValue).Append(" | ")
                .Append(value.BitCount).Append(" | ")
                .Append(value.Type).Append(" | ")
                .AppendLine(value.Num.ToString());
        }
        return text.ToString();
    }

    [ContextMenu("Build Head")]
    private void CreateModel(AvatarRecipeData data)
    {
        Transform parent = headParent != null ? headParent : transform;
        Transform previous = parent.Find("AvatarRecipeHead");
        GameObject head = AvatarHeadBuilder.Build(data);
        head.transform.SetParent(parent, false);
        if (previous != null)
        {
            previous.gameObject.SetActive(false);
            previous.SetParent(null, false);
            if (Application.isPlaying) Destroy(previous.gameObject);
            else DestroyImmediate(previous.gameObject);
        }
    }
}