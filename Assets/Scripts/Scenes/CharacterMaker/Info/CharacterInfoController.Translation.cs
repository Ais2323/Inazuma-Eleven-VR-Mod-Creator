using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using UnityEngine;
using UnityEngine.UI;

public partial class CharacterInfoController
{
    public Button autoTranslateButton;
    CancellationTokenSource identityTranslation;
    void InitializeTranslation() => autoTranslateButton.onClick.AddListener(OnClickAutoTranslate);
    public async void OnClickAutoTranslate()
    {
        if (identityTranslation != null || Identity == null) return;
        var identity = Identity;
        var source = identity.locales.FirstOrDefault(l => l.locale == detailLocale);
        if (source == null) return;
        string shirtName = source.shirtName;
        void CopyShirtName() {
            foreach (var target in identity.locales)
                if (target.locale != source.locale) target.shirtName = shirtName;
        }
        var fields = new Dictionary<string, string> { { "fullName", source.fullName }, { "shortName", source.shortName },
            { "description", source.description } };
        if (fields.Values.All(string.IsNullOrWhiteSpace)) {
            CopyShirtName(); RefreshIdentityFields();
            Debug.Log("[AutoTranslate][人物資訊] 沒有需翻譯的文字；制服名稱已原文複製到其他語系，未發送請求。"); return;
        }
        var cancellation = identityTranslation = new CancellationTokenSource();
        RefreshIdentityFields();
        try {
            Debug.Log("[AutoTranslate][人物資訊] 翻譯全名、暱稱與描述；制服名稱將直接原文複製，不送出翻譯。");
            var result = await AutoTranslationService.TranslateAsync(fields, source.locale, identity.locales.Select(l => l.locale), cancellation.Token,
                message => Debug.Log("[AutoTranslate][人物資訊] " + message));
            if (this == null || cancellation.IsCancellationRequested || !ReferenceEquals(identity, Identity)) { Debug.Log("[AutoTranslate][人物資訊] 已取消或切換角色，結果未套用。"); return; }
            foreach (var target in identity.locales) {
                if (!result.TryGetValue(target.locale, out var values)) continue;
                if (values.TryGetValue("fullName", out string full)) target.fullName = full;
                if (values.TryGetValue("shortName", out string shortName)) target.shortName = shortName;
                if (values.TryGetValue("description", out string description)) target.description = description;
                target.japaneseName = target.locale == "ja" ? target.fullName : null;
            }
            CopyShirtName();
            Debug.Log("[AutoTranslate][人物資訊] 已套用其他語系並原文複製制服名稱；來源語系保留。");
        } catch (OperationCanceledException) { Debug.Log("[AutoTranslate][人物資訊] 已取消，未套用部分結果。"); }
        catch (Exception e) { if (this != null) Debug.LogError(LocalizationManager.GetText("AutoTranslateFailed") + ": " + e.Message); }
        finally {
            if (ReferenceEquals(identityTranslation, cancellation)) identityTranslation = null;
            cancellation.Dispose(); if (this != null) RefreshIdentityFields();
        }
    }
    void CancelIdentityTranslation() => identityTranslation?.Cancel();
    void OnDisable() => CancelIdentityTranslation();
}
