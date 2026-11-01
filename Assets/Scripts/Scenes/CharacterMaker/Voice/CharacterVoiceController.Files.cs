using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using G4Composition;
using SFB;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;

[Serializable]
public sealed class VoiceCategoryRow
{
    public string category;
    public int page;
    public uint skillId;
    public string skillResource;
    [NonSerialized] public bool removed;
    [NonSerialized] public bool inherited;
    [NonSerialized] public string[] sourceCueNames;
    public Button preview, selectVoiceButton;
    public Text fileNames;
}

public partial class CharacterVoiceController
{
    bool importingVoice;
    int voiceSelectionRevision;

    async Task SelectVoiceFilesAsync(VoiceCategoryRow row)
    {
        if (importingVoice || row.removed || string.IsNullOrEmpty(row.category)) return;
        string[] paths = StandaloneFileBrowser.OpenFilePanel("Select voice files", "",
            new[] { new ExtensionFilter("Audio", "wav", "mp3", "ogg", "aiff", "aif", "hca") }, true);
        if (paths == null || paths.Length == 0 || string.IsNullOrWhiteSpace(paths[0])) return;
        var data = nowCharacterData;
        int revision = voiceSelectionRevision;
        string category = row.category;
        importingVoice = true;
        if (row.selectVoiceButton != null) row.selectVoiceButton.interactable = false;
        try
        {
            var files = new List<CharacterVoiceFile>();
            foreach (string path in paths)
            {
                if (row.removed || row.category != category) return;
                Debug.Log($"[VoicePanel] Import {row.category} [{files.Count + 1}/{paths.Length}] {Path.GetFileName(path)}", this);
                byte[] hca;
                if (Path.GetExtension(path).Equals(".hca", StringComparison.OrdinalIgnoreCase))
                {
                    hca = File.ReadAllBytes(path);
                    await Task.Run(() => HcaVoiceDecoder.Decode(hca, CancellationToken.None));
                    hca = await Task.Run(() => VoiceAudioEncoding.ToGameFormat(hca));
                }
                else
                {
                    AudioType type = Path.GetExtension(path).ToLowerInvariant() == ".mp3" ? AudioType.MPEG :
                        Path.GetExtension(path).ToLowerInvariant() == ".ogg" ? AudioType.OGGVORBIS :
                        Path.GetExtension(path).ToLowerInvariant() == ".wav" ? AudioType.WAV : AudioType.AIFF;
                    using (var request = UnityWebRequestMultimedia.GetAudioClip(new Uri(Path.GetFullPath(path)).AbsoluteUri, type))
                    {
                        var operation = request.SendWebRequest();
                        while (!operation.isDone) await Task.Delay(20);
                        if (request.result != UnityWebRequest.Result.Success) throw new InvalidDataException(request.error);
                        AudioClip clip = DownloadHandlerAudioClip.GetContent(request);
                        if (clip == null) throw new InvalidDataException("Cannot read audio: " + Path.GetFileName(path));
                        float[] samples; int rate, channels;
                        try
                        {
                            if (clip.length > 30 || clip.samples < 1) throw new InvalidDataException("Voice clips must be between 0 and 30 seconds.");
                            samples = new float[checked(clip.samples * clip.channels)]; rate = clip.frequency; channels = clip.channels;
                            if (!clip.GetData(samples, 0)) throw new InvalidDataException("Cannot decode audio: " + Path.GetFileName(path));
                        }
                        finally { CharacterManager.DestroyRuntimeObject(clip); }
                        hca = await Task.Run(() => VoiceAudioEncoding.Encode(samples, rate, channels));
                    }
                }
                files.Add(new CharacterVoiceFile { fileName = Path.GetFileName(path), hcaBase64 = Convert.ToBase64String(hca) });
            }
            if (this == null || manager == null || row.removed || row.category != category || !ReferenceEquals(data, nowCharacterData) || revision != voiceSelectionRevision) return;
            data.voiceOverrides ??= new List<CharacterVoiceOverride>();
            data.voiceOverrides.RemoveAll(v => v.category == row.category);
            data.voiceOverrides.Add(new CharacterVoiceOverride { category = row.category, page = row.page, skillId = row.skillId, skillResource = row.skillResource, files = files });
            StopVoicePreview(); RefreshVoiceFileLabels();
        }
        catch (Exception exception)
        {
            if (this != null) { voicePreviewError = exception.Message; Debug.LogWarning("Voice import: " + exception.Message, this); }
        }
        finally
        {
            importingVoice = false;
            if (row.selectVoiceButton != null) row.selectVoiceButton.interactable = true;
        }
    }

    void RefreshVoiceFileLabels()
    {
        if (manager == null) return;
        foreach (var row in AllVoiceRows())
        {
            var selected = nowCharacterData.voiceOverrides?.Find(v => v.category == row.category);
            if (row.fileNames != null) row.fileNames.text = selected?.files?.Count > 0 ? string.Join(",", selected.files.Select(f => f.fileName)) :
                string.Join(",", row.sourceCueNames ?? Array.Empty<string>());
        }
    }
}
