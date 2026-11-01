using System;
using System.Collections.Generic;
using System.IO;
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
    readonly Dictionary<string, int> voiceGroupPositions = new Dictionary<string, int>();
    string cueBankPath;
    VoiceCueBank cueBank;

    public async void OnClickPreview() => await PreviewVoiceAsync();

    public async Task PreviewVoiceAsync()
        => await PreviewVoiceAsync(null);

    async Task PreviewVoiceAsync(string group)
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
            var custom = string.IsNullOrEmpty(group) ? null : nowCharacterData.voiceOverrides?.Find(v => v.category == group);
            string[] replacementAudio = custom?.files?.ConvertAll(f => f.hcaBase64).ToArray();
            string avatarVoice = string.IsNullOrWhiteSpace(code) ? null :
                AvatarRecipeParser.ResolveVoiceName(AvatarRecipeParser.Parse(code));
            int random = UnityEngine.Random.Range(0, int.MaxValue);
            string previousBank = lastVoiceBank;
            int previousWave = lastVoiceWave;
            string playedCue = null;
            int playedPosition = 0, totalClips = 0;
            var result = await Task.Run(() => {
                string bank = VoicePreviewAudio.ResolveBank(common, sourceId, avatarVoice);
                if (!string.IsNullOrEmpty(group))
                {
                    lock (voiceGroupPositions)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        if (replacementAudio != null && replacementAudio.Length > 0)
                        {
                            string customKey = "custom|" + group;
                            voiceGroupPositions.TryGetValue(customKey, out int current);
                            playedPosition = current % replacementAudio.Length;
                            var audio = HcaVoiceDecoder.Decode(Convert.FromBase64String(replacementAudio[playedPosition]), cancellation.Token);
                            playedCue = "custom/" + group;
                            totalClips = replacementAudio.Length;
                            voiceGroupPositions[customKey] = (playedPosition + 1) % replacementAudio.Length;
                            return new VoicePreviewAudio.Result { Bank = bank, WaveId = -1, Samples = audio.Samples, SampleRate = audio.SampleRate, Channels = audio.Channels };
                        }
                        // The panel's buttons represent cue families, not AWB IDs.
                        if (cueBankPath != bank)
                        {
                            cueBank = new VoiceCueBank(Path.ChangeExtension(bank, ".acb"));
                            cueBankPath = bank;
                        }
                        var clips = cueBank.Category(group);
                        if (clips.Count == 0) throw new InvalidDataException("此角色沒有 " + group + " 語音。");
                        string key = bank + "|" + group;
                        voiceGroupPositions.TryGetValue(key, out int position);
                        playedPosition = position % clips.Count;
                        var clip = clips[playedPosition];
                        playedCue = clip.Name;
                        totalClips = clips.Count;
                        var decoded = VoicePreviewAudio.DecodeWave(bank, clip.WaveId, cancellation.Token);
                        cancellation.Token.ThrowIfCancellationRequested();
                        voiceGroupPositions[key] = (playedPosition + 1) % clips.Count;
                        return decoded;
                    }
                }
                return VoicePreviewAudio.Decode(bank, random,
                    bank == previousBank ? previousWave : -1, cancellation.Token);
            }, cancellation.Token);
            if (this == null || cancellation.IsCancellationRequested || !gameObject.activeInHierarchy) return;
            lastVoiceBank = result.Bank;
            lastVoiceWave = result.WaveId;
            if (playedCue != null) Debug.Log($"[VoicePanel] {group} [{playedPosition + 1}/{totalClips}] {playedCue} / AWB {result.WaveId}", this);
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
        bool available =
            !string.IsNullOrWhiteSpace(nowCharacterData.voiceCharacterId) ||
            !string.IsNullOrWhiteSpace(nowCharacterData.voiceAvatarCode);
        if (voicePreviewButton != null) voicePreviewButton.interactable = available;
        if (voiceSettingButton != null) voiceSettingButton.interactable = available;
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

    void Update()
    {
        if (manager != null) RefreshVoicePreviewButton();
        if (voicePanelWasOpen && voicePanel != null && !voicePanel.activeInHierarchy)
        { StopVoicePreview(); CleanupEmptyVoiceSkillRows(); voicePanelWasOpen = false; }
    }
    void OnDisable() => StopVoicePreview();
}
