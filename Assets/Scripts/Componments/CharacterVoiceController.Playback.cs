using System;
using System.Threading;
using System.Threading.Tasks;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

public partial class CharacterVoiceController
{
    public Button voicePreviewButton;
    public string voicePreviewError;
    public bool IsVoicePreviewLoading => voicePreviewCancellation != null;
    CancellationTokenSource voicePreviewCancellation;
    AudioSource voicePreviewSource;
    AudioClip voicePreviewClip;
    string lastVoiceBank;
    int lastVoiceWave = -1;

    public async void OnClickPreview() => await PreviewVoiceAsync();

    public async Task PreviewVoiceAsync()
    {
        StopVoicePreview();
        var cancellation = new CancellationTokenSource();
        voicePreviewCancellation = cancellation;
        voicePreviewError = "";
        try
        {
            string common = DumpDataManager.CommonPath;
            string sourceId = nowCharacterData.voiceCharacterId;
            string code = nowCharacterData.voiceAvatarCode;
            string avatarVoice = string.IsNullOrWhiteSpace(code) ? null :
                AvatarRecipeParser.ResolveVoiceName(AvatarRecipeParser.Parse(code));
            int random = UnityEngine.Random.Range(0, int.MaxValue);
            string previousBank = lastVoiceBank;
            int previousWave = lastVoiceWave;
            var result = await Task.Run(() => {
                string bank = VoicePreviewAudio.ResolveBank(common, sourceId, avatarVoice);
                return VoicePreviewAudio.Decode(bank, random,
                    bank == previousBank ? previousWave : -1, cancellation.Token);
            }, cancellation.Token);
            if (this == null || cancellation.IsCancellationRequested || !gameObject.activeInHierarchy) return;
            lastVoiceBank = result.Bank;
            lastVoiceWave = result.WaveId;
            if (voicePreviewSource == null)
            {
                voicePreviewSource = gameObject.AddComponent<AudioSource>();
                voicePreviewSource.playOnAwake = false;
                voicePreviewSource.spatialBlend = 0;
                voicePreviewSource.loop = false;
            }
            voicePreviewClip = AudioClip.Create("Voice " + result.WaveId, result.Samples.Length / result.Channels, result.Channels,
                result.SampleRate, false);
            voicePreviewClip.SetData(result.Samples, 0);
            voicePreviewSource.clip = voicePreviewClip;
            voicePreviewSource.Play();
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (this != null && !cancellation.IsCancellationRequested)
            {
                voicePreviewError = exception.Message;
                Debug.LogWarning("語音預覽失敗：" + exception.Message, this);
            }
        }
        finally
        {
            if (voicePreviewCancellation == cancellation) voicePreviewCancellation = null;
            cancellation.Dispose();
        }
    }

    internal void RefreshVoicePreviewButton()
    {
        if (voicePreviewButton != null) voicePreviewButton.interactable =
            !string.IsNullOrWhiteSpace(nowCharacterData.voiceCharacterId) ||
            !string.IsNullOrWhiteSpace(nowCharacterData.voiceAvatarCode);
    }

    void StopVoicePreview()
    {
        voicePreviewCancellation?.Cancel();
        voicePreviewCancellation = null;
        if (voicePreviewSource != null)
        {
            voicePreviewSource.Stop();
            voicePreviewSource.clip = null;
        }
        if (voicePreviewClip != null) CharacterManager.DestroyRuntimeObject(voicePreviewClip);
        voicePreviewClip = null;
    }

    void Update() { if (manager != null) RefreshVoicePreviewButton(); }
    void OnDisable() => StopVoicePreview();
}
