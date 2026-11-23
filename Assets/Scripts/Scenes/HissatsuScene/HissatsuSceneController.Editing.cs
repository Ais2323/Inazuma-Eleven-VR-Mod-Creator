using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using SFB;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class HissatsuSceneController
{
    [Header("Motion playlist")]
    public Transform clipContent;
    public GameObject clipTemplate;
    public Button clipAddButton,addButton,saveButton,loadButton;
    [Header("Editing modes")]
    public Transform modeButtonsParent,infoLocaleButtonsParent;
    public GameObject infoPanel,clipPanel;
    public InputField skillNameInput,skillDescriptionInput;
    public Text skillTargetText,clipSelectionText;
    public Toggle sellSkillToggle,freeSkillToggle;
    public InputField skillShopPriceInput;
    HissatsuMakerDocument settings;
    int selectedClip=-1,editorMode;
    string infoLocale;
    readonly List<Button> modes=new(),infoLocales=new();
    readonly List<GameObject> clipRows=new();
    HissatsuClipSequence Sequence=>settings?.sequences.FirstOrDefault(s=>s.eventName==active?.Event.Name);
    float PlaybackDuration=>Sequence?.Duration??active?.Duration??0;
    float CurrentSourceTime=>Sequence?.SourceTime(position)??position;

    void InitializeEditing()
    {
        if(clipTemplate!=null)clipTemplate.SetActive(false);
        if(clipAddButton!=null)clipAddButton.onClick.AddListener(OpenClipPicker);
        InitializeClipPicker();
        if(addButton!=null)addButton.onClick.AddListener(OnClickAdd);
        if(saveButton!=null)saveButton.onClick.AddListener(OnClickSave);
        if(loadButton!=null)loadButton.onClick.AddListener(OnClickLoad);
        if(modeButtonsParent!=null)foreach(Transform child in modeButtonsParent){var button=child.GetComponent<Button>();if(button==null)continue;int index=modes.Count;modes.Add(button);button.onClick.AddListener(()=>SelectMode(index));}
        if(infoLocaleButtonsParent!=null)foreach(Transform child in infoLocaleButtonsParent){var button=child.GetComponent<Button>();if(button==null)continue;string language=child.name;infoLocales.Add(button);button.onClick.AddListener(()=>{infoLocale=language;RefreshInfo();});}
        if(skillNameInput!=null)skillNameInput.onValueChanged.AddListener(value=>{var item=CurrentInfo;if(item!=null)item.name=value;});
        if(skillDescriptionInput!=null)skillDescriptionInput.onValueChanged.AddListener(value=>{var item=CurrentInfo;if(item!=null)item.description=value;});
        if(sellSkillToggle!=null)sellSkillToggle.onValueChanged.AddListener(value=>{if(settings!=null)settings.sellInShop=value;RefreshShopControls();});
        if(freeSkillToggle!=null)freeSkillToggle.onValueChanged.AddListener(value=>{if(settings!=null)settings.freeInShop=value;RefreshShopControls();});
        if(skillShopPriceInput!=null)skillShopPriceInput.onEndEdit.AddListener(value=>{if(settings!=null&&int.TryParse(value,out int price)&&price>0)settings.shopPrice=price;RefreshShopControls();});
        RefreshInfo();RefreshModes();
    }
    HissatsuLocalizedInfo CurrentInfo=>settings?.info.FirstOrDefault(i=>i.locale==infoLocale);
    void PrepareEditing(HissatsuCatalog.Entry entry)
    {
        if(settings==null||settings.sourceSkillId!=entry.Id){
            settings=new HissatsuMakerDocument {sourceSkillId=entry.Id,sourceResource=entry.Resource,info=HissatsuInfoCatalog.Read(entry).ToList()};
            infoLocale=UniformCatalog.Locale;
        }
        settings.alternateAnimation=alternateAnimation;
        if(Sequence==null)settings.sequences.Add(HissatsuClipSequence.From(active.Event));
        selectedClip=-1;editorMode=0;addingClip=false;BuildClipRows();RefreshInfo();RefreshModes();
    }
    void RefreshInfo()
    {
        if(settings!=null&&!settings.info.Any(i=>i.locale==infoLocale))infoLocale=settings.info.FirstOrDefault()?.locale;
        var value=CurrentInfo;
        if(skillNameInput!=null){skillNameInput.interactable=value!=null;skillNameInput.SetTextWithoutNotify(value?.name??"");}
        if(skillDescriptionInput!=null){skillDescriptionInput.interactable=value!=null;skillDescriptionInput.SetTextWithoutNotify((value?.description??"").Replace("\\n","\n"));}
        foreach(var button in infoLocales)button.interactable=settings!=null&&button.name!=infoLocale&&settings.info.Any(i=>i.locale==button.name);
        if(skillTargetText!=null)skillTargetText.text=settings==null?"":settings.isNew?LocalizationManager.GetText("HissatsuNewSkill",gameObject.scene.name):settings.sourceResource;
        if(addButton!=null)addButton.interactable=settings!=null&&!settings.isNew;
        if(saveButton!=null)saveButton.interactable=settings!=null;
        if(clipAddButton!=null)clipAddButton.interactable=settings!=null;
        if(loadButton!=null)loadButton.interactable=catalog!=null;
        RefreshShopControls();
    }
    void RefreshShopControls()
    {
        if(sellSkillToggle!=null){sellSkillToggle.interactable=settings!=null;sellSkillToggle.SetIsOnWithoutNotify(settings?.sellInShop??false);}
        if(freeSkillToggle!=null){freeSkillToggle.interactable=settings?.sellInShop==true;freeSkillToggle.SetIsOnWithoutNotify(settings?.freeInShop??false);}
        if(skillShopPriceInput!=null){skillShopPriceInput.interactable=settings?.sellInShop==true&&!settings.freeInShop;skillShopPriceInput.SetTextWithoutNotify((settings?.shopPrice??1).ToString());}
    }
    internal void SelectClip(int index)
    {
        if(Sequence==null||index<0||index>=Sequence.clips.Count)return;
        addingClip=false;selectedClip=index;playing=false;position=Sequence.StartOf(index);editorMode=1;
        EvaluateActive(position);RefreshModes();UpdateControls();
    }
    internal void MoveClip(int index,int delta)
    {
        var sequence=Sequence;if(sequence==null||index<0||index>=sequence.clips.Count||index+delta<0||index+delta>=sequence.clips.Count)return;
        addingClip=false;selectedClip=sequence.Move(index,delta);playing=false;position=sequence.StartOf(selectedClip);editorMode=1;
        BuildClipRows();EvaluateActive(position);RefreshModes();UpdateControls();
    }
    void BuildClipRows()
    {
        foreach(var row in clipRows){row.SetActive(false);CharacterManager.DestroyRuntimeObject(row);}clipRows.Clear();
        if(Sequence==null||clipTemplate==null||clipContent==null)return;
        for(int i=0;i<Sequence.clips.Count;i++){
            int index=i;var clip=Sequence.clips[i];var row=Instantiate(clipTemplate,clipContent,false);row.name="Motion_"+clip.instanceId;row.SetActive(true);clipRows.Add(row);
            var select=row.transform.Find("GroupButton").GetComponent<Button>();select.GetComponentInChildren<Text>(true).text=$"{i+1:00}  {clip.motion}\n{clip.Duration:0.00} s";
            select.onClick.AddListener(()=>SelectClip(index));
            var previous=row.transform.Find("MovePrev").GetComponent<Button>();previous.interactable=i>0;previous.onClick.AddListener(()=>MoveClip(index,-1));
            var next=row.transform.Find("MoveNext").GetComponent<Button>();next.interactable=i<Sequence.clips.Count-1;next.onClick.AddListener(()=>MoveClip(index,1));
        }
    }
    internal void SelectMode(int index)
    {
        if(index<0||index>=modes.Count||index>0&&selectedClip<0)return;
        addingClip=false;editorMode=index;if(index==0)selectedClip=-1;RefreshModes();
    }
    void RefreshModes()
    {
        for(int i=0;i<modes.Count;i++)modes[i].interactable=(i!=editorMode||addingClip)&&(i==0||selectedClip>=0);
        if(infoPanel!=null)infoPanel.SetActive(editorMode==0&&!addingClip);
        if(clipPanel!=null)clipPanel.SetActive(editorMode>0&&selectedClip>=0&&!addingClip);
        if(clipSelectionText!=null)clipSelectionText.text=selectedClip>=0&&Sequence!=null?Sequence.clips[selectedClip].motion+"\n"+LocalizationManager.GetText("HissatsuClipEditingPending",gameObject.scene.name):"";
        if(clipPickerPanel!=null)clipPickerPanel.SetActive(addingClip);
        if(!addingClip&&clipSkillPicker!=null)clipSkillPicker.Close();
        if(removeClipButton!=null)removeClipButton.interactable=selectedClip>=0&&!addingClip;
        for(int i=0;i<clipRows.Count;i++)clipRows[i].transform.Find("GroupButton").GetComponent<Button>().interactable=i!=selectedClip;
    }
    public void OnClickAdd()
    {
        if(settings==null||settings.isNew)return;
        settings.isNew=true;settings.newKey=Guid.NewGuid().ToString("N");RefreshInfo();UpdateButtons();
    }
    internal HissatsuMakerDocument CreateDocument()
    {
        if(settings==null)throw new InvalidOperationException("Select a skill first.");
        settings.alternateAnimation=alternateAnimation;settings.Upgrade();settings.Validate();
        return JsonUtility.FromJson<HissatsuMakerDocument>(JsonUtility.ToJson(settings));
    }
    public void OnClickSave()
    {
        if(settings==null||Loading.IsRunning)return;
        string name=CurrentInfo?.name??settings.sourceResource;
        name=new string(name.Where(c=>!Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        string path=StandaloneFileBrowser.SaveFilePanel("Save HissatsuMaker","",name,"hissatsumaker");
        if(string.IsNullOrWhiteSpace(path))return;
        Loading.Perform(()=>{if(!path.EndsWith(".hissatsumaker",StringComparison.OrdinalIgnoreCase))path+=".hissatsumaker";CreateDocument().Write(path);AppLog.Result("HissatsuMaker saved: "+path);},"LoadingSave");
    }
    public void OnClickLoad()
    {
        if(catalog==null||Loading.IsRunning)return;
        var paths=StandaloneFileBrowser.OpenFilePanel("Load HissatsuMaker","","hissatsumaker",false);
        if(paths==null||paths.Length==0||string.IsNullOrWhiteSpace(paths[0]))return;
        try{var doc=HissatsuMakerDocument.Read(paths[0]);Loading.Run(()=>RestoreDocument(doc));}
        catch(Exception error){AppLog.Error("[HissatsuMaker] "+error.Message,this);}
    }
    internal IEnumerator RestoreDocument(HissatsuMakerDocument doc)
    {
        doc.Validate();
        var entry=catalog.Entries.FirstOrDefault(e=>e.Id==doc.sourceSkillId&&e.Resource==doc.sourceResource)
            ??throw new InvalidDataException("Saved skill is missing from this game data: "+doc.sourceResource);
        if(doc.alternateAnimation&&string.IsNullOrEmpty(entry.AlternateEvent))throw new InvalidDataException("Saved alternate animation is unavailable.");
        foreach(var sequence in doc.sequences){
            if(sequence.eventName!=entry.Event&&sequence.eventName!=entry.AlternateEvent)throw new InvalidDataException("Saved event does not belong to this skill.");
            foreach(var clip in sequence.clips.Where(c=>!string.IsNullOrEmpty(c.sourceEvent))){
                var source=catalog.Entries.FirstOrDefault(e=>e.Id==clip.sourceSkillId&&e.Resource==clip.sourceResource);
                if(source==null||clip.sourceEvent!=source.Event&&clip.sourceEvent!=source.AlternateEvent||(clip.assignedModel??"")!=(source.AssignedModel??"")||(clip.assignedPath??"")!=(source.AssignedPath??""))
                    throw new InvalidDataException("Missing or invalid imported motion source: "+clip.sourceResource);
            }
            sequence.ValidateSource(HissatsuEvent.Read(sequence.eventName));
        }
        string selectedEvent=doc.alternateAnimation?entry.AlternateEvent:entry.Event;
        if(!doc.sequences.Any(s=>s.eventName==selectedEvent))throw new InvalidDataException("Missing saved motion playlist.");
        // Validate all references before changing the current selection.
        selectionLoading=true;playing=false;dragging=false;resumeAfterDrag=false;
        try{
        settings=JsonUtility.FromJson<HissatsuMakerDocument>(JsonUtility.ToJson(doc));infoLocale=UniformCatalog.Locale;
        yield return PopulateCategory(entry.Category);
        yield return LoadSelection(entry,doc.alternateAnimation,++ticket);
        playing=false;position=0;EvaluateActive(0);UpdateControls();RefreshInfo();
        AppLog.Result("HissatsuMaker loaded: "+doc.sourceResource);
        }finally{selectionLoading=false;}
    }
}
