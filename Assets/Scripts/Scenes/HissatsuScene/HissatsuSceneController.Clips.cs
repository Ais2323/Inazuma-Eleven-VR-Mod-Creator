using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class HissatsuSceneController
{
    [Header("Import motion clips")]
    public GameObject clipPickerPanel;
    public Dropdown clipAnimationDropdown;
    public Button clipSkillButton;
    public HissatsuSkillPicker clipSkillPicker;
    public Transform clipPickerContent;
    public Button clipPickerTemplate,newMotionButton,removeClipButton;
    bool addingClip;
    readonly List<Button> pickerRows=new();
    HissatsuCatalog.Entry pickerSkill;
    HissatsuEvent pickerEvent;
    readonly Dictionary<string,HissatsuPreview> importedPreviews=new();
    HissatsuPreview renderedPreview;

    void InitializeClipPicker()
    {
        if(clipAnimationDropdown!=null){clipAnimationDropdown.ClearOptions();clipAnimationDropdown.AddOptions(new[]{"HissatsuNormalAnimation","HissatsuAlternateAnimation"}.Select(k=>LocalizationManager.GetText(k,gameObject.scene.name)).ToList());}
        if(clipPickerTemplate!=null)clipPickerTemplate.gameObject.SetActive(false);
        if(newMotionButton!=null)newMotionButton.interactable=false;
        if(removeClipButton!=null)removeClipButton.onClick.AddListener(RemoveSelectedClip);
        if(clipSkillButton!=null)clipSkillButton.onClick.AddListener(OpenSourceSkillPicker);
        RefreshPickerSkillLabel();
        if(clipAnimationDropdown!=null)clipAnimationDropdown.onValueChanged.AddListener(_=>RefreshPickerMotions());
    }
    public void OpenClipPicker()
    {
        if(settings==null||Loading.IsRunning)return;
        addingClip=true;selectedClip=-1;playing=false;RefreshModes();UpdateControls();RefreshPickerMotions();OpenSourceSkillPicker();
    }
    public void OpenSourceSkillPicker()
    {
        if(catalog==null||clipSkillPicker==null||!addingClip||Loading.IsRunning)return;
        int request=ticket;
        clipSkillPicker.Open(catalog.Entries,pickerSkill?.Category??category,pickerSkill,
            ()=>isActiveAndEnabled&&settings!=null&&addingClip&&request==ticket,
            entry=>{
                pickerSkill=entry;clipAnimationDropdown?.SetValueWithoutNotify(0);
                RefreshPickerMotions();
            });
    }
    void RefreshPickerSkillLabel()
    {
        if(clipSkillButton!=null)clipSkillButton.GetComponentInChildren<Text>(true).text=pickerSkill==null?
            LocalizationManager.GetText("HissatsuSelectSourceSkill",gameObject.scene.name):pickerSkill.Label+" / "+pickerSkill.Resource;
    }
    void RefreshPickerMotions()
    {
        foreach(var row in pickerRows){row.gameObject.SetActive(false);CharacterManager.DestroyRuntimeObject(row.gameObject);}pickerRows.Clear();pickerEvent=null;
        RefreshPickerSkillLabel();
        if(pickerSkill==null){if(clipAnimationDropdown!=null)clipAnimationDropdown.interactable=false;return;}
        var entry=pickerSkill;bool alternate=clipAnimationDropdown!=null&&clipAnimationDropdown.value==1&&!string.IsNullOrEmpty(entry.AlternateEvent);
        if(clipAnimationDropdown!=null){clipAnimationDropdown.interactable=!string.IsNullOrEmpty(entry.AlternateEvent);if(!alternate)clipAnimationDropdown.SetValueWithoutNotify(0);}
        try{pickerEvent=HissatsuEvent.Read(alternate?entry.AlternateEvent:entry.Event);}
        catch(Exception error){AppLog.Error("[Hissatsu motion] "+error.Message,this);return;}
        if(clipPickerContent==null||clipPickerTemplate==null)return;
        for(int i=0;i<pickerEvent.Cuts.Count;i++){
            int sourceIndex=i;var cut=pickerEvent.Cuts[i];var row=Instantiate(clipPickerTemplate,clipPickerContent,false);row.name="SourceMotion_"+i;
            row.GetComponentInChildren<Text>(true).text=$"{i+1:00} {cut.Name}  {(cut.End-cut.Start)/60f:0.00} s";row.gameObject.SetActive(true);pickerRows.Add(row);
            bool sourceAlternate=alternate;row.onClick.AddListener(()=>{if(!Loading.IsRunning)Loading.Run(()=>AppendClip(entry,sourceAlternate,sourceIndex));});
        }
        var scroll=clipPickerContent.GetComponentInParent<ScrollRect>();if(scroll!=null)scroll.verticalNormalizedPosition=1;
    }
    internal IEnumerator AppendClip(HissatsuCatalog.Entry source,bool alternate,int index)
    {
        var sequence=Sequence;if(sequence==null)yield break;
        var ev=HissatsuEvent.Read(alternate?source.AlternateEvent:source.Event);
        if(index<0||index>=ev.Cuts.Count)throw new ArgumentOutOfRangeException(nameof(index));
        var clip=HissatsuClipSequence.From(ev).clips[index].Copy();
        clip.instanceId=Guid.NewGuid().ToString("N");clip.sourceEvent=ev.Name;clip.sourceSkillId=source.Id;clip.sourceResource=source.Resource;
        clip.assignedModel=source.AssignedModel;clip.assignedPath=source.AssignedPath;clip.neutralIncomingBall=source.Category==3||(source.Category==2&&alternate);
        int request=ticket;yield return EnsureImportedPreview(clip,request);
        if(request!=ticket||Sequence!=sequence||!isActiveAndEnabled)yield break;
        sequence.clips.Add(clip);addingClip=false;BuildClipRows();SelectClip(sequence.clips.Count-1);RefreshInfo();
        progress.interactable=playButton.interactable=true;
    }
    public void RemoveSelectedClip()
    {
        var sequence=Sequence;if(sequence==null||selectedClip<0||selectedClip>=sequence.clips.Count)return;
        sequence.clips.RemoveAt(selectedClip);int next=Mathf.Min(selectedClip,sequence.clips.Count-1);
        selectedClip=-1;playing=false;addingClip=false;BuildClipRows();
        if(next>=0)SelectClip(next);
        else{editorMode=0;position=0;EvaluateActive(0);RefreshModes();UpdateControls();}
        progress.interactable=playButton.interactable=sequence.clips.Count>0;
    }
    static string ClipPreviewKey(HissatsuMotionClip clip)=>clip.sourceEvent+"|"+clip.assignedModel+"|"+clip.assignedPath+"|"+clip.neutralIncomingBall;
    IEnumerator EnsureImportedPreviews(HissatsuClipSequence sequence,int request)
    {
        if(sequence==null)yield break;
        foreach(var clip in sequence.clips.Where(c=>!string.IsNullOrEmpty(c.sourceEvent)).GroupBy(ClipPreviewKey).Select(g=>g.First()))
            yield return EnsureImportedPreview(clip,request);
    }
    IEnumerator EnsureImportedPreview(HissatsuMotionClip clip,int request)
    {
        string key=ClipPreviewKey(clip);if(importedPreviews.ContainsKey(key))yield break;
        var preview=new HissatsuPreview(HissatsuEvent.Read(clip.sourceEvent),modelParent,previewCamera,string.IsNullOrEmpty(clip.assignedModel)?null:clip.assignedModel,string.IsNullOrEmpty(clip.assignedPath)?null:clip.assignedPath,clip.neutralIncomingBall);bool stored=false;
        try{
            var loader=preview.Load(()=>request!=ticket||!isActiveAndEnabled);
            try{while(loader.MoveNext()){if(request!=ticket||!isActiveAndEnabled)yield break;yield return loader.Current;}}
            finally{(loader as IDisposable)?.Dispose();}
            if(request!=ticket||!isActiveAndEnabled)yield break;
            importedPreviews.Add(key,preview);stored=true;
            foreach(string warning in preview.Warnings.Distinct())AppLog.Warning("[Hissatsu imported motion] "+clip.sourceResource+" / "+warning);
        }finally{if(!stored)preview.Dispose();}
    }
    void ShowPlaybackPreview(HissatsuPreview preview)
    {
        if(renderedPreview!=preview){if(renderedPreview?.Root!=null)renderedPreview.Root.SetActive(false);renderedPreview=preview;lastLoggedCut=null;}
        if(active!=preview&&active?.Root!=null)active.Root.SetActive(false);
        if(preview?.Root!=null)preview.Root.SetActive(true);
    }
    void ClearImportedPreviews()
    {
        if(importedPreviews.Values.Contains(renderedPreview))renderedPreview=null;
        foreach(var preview in importedPreviews.Values)preview.Dispose();importedPreviews.Clear();
    }
}
