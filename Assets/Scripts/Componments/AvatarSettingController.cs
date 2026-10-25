using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using G4Composition;
using SFB;
using UnityEngine;
using UnityEngine.UI;

public class AvatarSettingController : MonoBehaviour
{
    public InputField inputField;
    public string nowAvatarCode = "";
    public Transform targetShowParent;
    public GameObject targetModel;

    [TextArea] public string recognizedText;
    public string lastError;
    public bool IsReadingImage { get; private set; }
    private CancellationTokenSource imageReadCancellation;

    public async void OnClickLoadImage()
    {
        if (IsReadingImage) return;
        try
        {
            string[] paths = StandaloneFileBrowser.OpenFilePanel("選擇角色編號圖片", "",
                new[] { new ExtensionFilter("圖片", "png", "jpg", "jpeg", "bmp") }, false);
            if (paths != null && paths.Length > 0) await ReadImageAsync(paths[0]);
        }
        catch (Exception exception) { ReportError("讀取圖片失敗", exception); }
    }

    /// <summary>Recognize the code and fill the input without changing the displayed avatar.</summary>
    public async Task ReadImageAsync(string path)
    {
        if (IsReadingImage) return;
        if (inputField == null)
        {
            ReportError("讀取圖片失敗", new InvalidOperationException("尚未設定 inputField。"));
            return;
        }
        var cancellation = new CancellationTokenSource();
        imageReadCancellation = cancellation;
        IsReadingImage = true;
        lastError = "";
        recognizedText = "";
        try
        {
            var result = await AvatarRecipeImageReader.ReadAsync(path, cancellation.Token);
            if (this == null || cancellation.IsCancellationRequested) return;
            recognizedText = string.Join("\n\n", result.candidates ?? Array.Empty<string>());
            if (!string.IsNullOrEmpty(result.error)) throw new InvalidDataException(result.error);
            string code = AvatarRecipeParser.ExtractImageCode(result.candidates);
            if (inputField == null) throw new InvalidOperationException("inputField 已被移除。");
            // Do not invoke UI listeners that might automatically rebuild a model.
            inputField.SetTextWithoutNotify(code);
        }
        catch (OperationCanceledException) { }
        catch (Exception exception) { ReportError("辨識圖片失敗", exception); }
        finally
        {
            cancellation.Dispose();
            if (this != null)
            {
                imageReadCancellation = null;
                IsReadingImage = false;
            }
        }
    }

    public void OnClickUpdate()
    {
        try
        {
            if (inputField == null) throw new InvalidOperationException("尚未設定 inputField。");
            if (targetShowParent == null) throw new InvalidOperationException("尚未設定 targetShowParent。");
            LoadCode(inputField.text);
        }
        catch (Exception exception) { ReportError("更新角色失敗", exception); }
    }

    public void RestoreState(string code, string input)
    {
        imageReadCancellation?.Cancel();
        if (!string.IsNullOrWhiteSpace(code)) LoadCode(code);
        else
        {
            if (targetModel != null)
            {
                targetModel.SetActive(false);
                targetModel.transform.SetParent(null, false);
                if (Application.isPlaying) Destroy(targetModel); else DestroyImmediate(targetModel);
            }
            targetModel = null;
            nowAvatarCode = "";
            lastError = "";
        }
        if (inputField != null) inputField.SetTextWithoutNotify(input ?? nowAvatarCode ?? "");
    }

    public void LoadCode(string code)
    {
        if (targetShowParent == null) throw new InvalidOperationException("尚未設定 targetShowParent。");
        var recipe = AvatarRecipeParser.Parse(code);
        // Build separately so a bad code or failed build keeps the old preview.
        GameObject head = AvatarHeadBuilder.Build(recipe);
        while (targetShowParent.childCount > 0)
        {
            Transform previous = targetShowParent.GetChild(0);
            previous.gameObject.SetActive(false);
            previous.SetParent(null, false);
            if (Application.isPlaying) Destroy(previous.gameObject);
            else DestroyImmediate(previous.gameObject);
        }
        head.transform.SetParent(targetShowParent, false);
        targetModel = head;
        nowAvatarCode = recipe.Code;
        if (inputField != null) inputField.SetTextWithoutNotify(recipe.Code);
        lastError = "";
    }

    private void ReportError(string operation, Exception exception)
    {
        if (this == null) return;
        lastError = operation + "：" + exception.Message;
        Debug.LogWarning(lastError, this);
    }

    private void OnDestroy() => imageReadCancellation?.Cancel();
}