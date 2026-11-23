using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class HissatsuSceneController : MonoBehaviour
{
    public Transform categoryParent,content,modelParent;
    public Button itemTemplate,playButton;
    public GameObject playIcon,pauseIcon;
    public GameObject animationVariantParent;
    public Button normalAnimationButton,alternateAnimationButton;
    public Text alternateAnimationText;
    public Slider progress;
    public Text timeText,playText;
    public Camera previewCamera;
    public RawImage display;
    HissatsuCatalog catalog;
    readonly List<Button> categories=new(),items=new();
    readonly Dictionary<string,HissatsuPreview> cache=new();
    readonly List<string> recent=new();
    HissatsuPreview active;
    HissatsuStadium stadium;
    HissatsuCatalog.Entry selected;
    List<HissatsuCatalog.Entry> filtered=new();
    int category,ticket,categoryTicket;
    float position;
    bool alternateAnimation,selectionLoading;
    bool playing,dragging,resumeAfterDrag,focused=true;
    RenderTexture texture;
    Material displayMaterial,previousDisplayMaterial;
    string locale,lastLoggedCut;
    IEnumerator Start()=>Loading.Track(Initialize(),"LoadingInitialize");
    IEnumerator Initialize()
    {
        InitializeEditing();
        foreach(Transform child in categoryParent){var button=child.GetComponent<Button>();if(button==null)continue;int at=categories.Count;categories.Add(button);button.onClick.AddListener(()=>SelectCategory(at));}
        playButton.onClick.AddListener(TogglePlay);progress.onValueChanged.AddListener(Seek);
        normalAnimationButton?.onClick.AddListener(()=>SelectAnimation(false));
        alternateAnimationButton?.onClick.AddListener(()=>SelectAnimation(true));UpdateAnimationButtons();
        progress.minValue=0;progress.maxValue=1;progress.interactable=false;playButton.interactable=false;
        if(!DumpDataManager.ValidateSavedPath()){AppLog.Error("[Hissatsu] "+DumpDataManager.LastError);yield break;}
        stadium=new HissatsuStadium();
        var stadiumLoad=stadium.Load(modelParent);
        try{while(true){
            bool more;
            try{more=stadiumLoad.MoveNext();}
            catch(Exception e){stadium.Dispose();stadium=null;AppLog.Warning("[Hissatsu stadium] "+e.Message);break;}
            if(!more)break;yield return stadiumLoad.Current;
        }}
        finally{(stadiumLoad as IDisposable)?.Dispose();}
        Loading.UpdateText(LocalizationManager.GetText("HissatsuCatalogLoading"));yield return null;
        catalog=new HissatsuCatalog();locale=UniformCatalog.Locale;yield return PopulateCategory(0);UpdateControls();RefreshInfo();
    }
    public void SelectCategory(int value)
    {
        if(catalog!=null)StartCoroutine(Loading.Track(PopulateCategory(value),"HissatsuCatalogLoading"));
    }
    IEnumerator PopulateCategory(int value)
    {
        if(catalog==null)yield break;int request=++categoryTicket;category=value;
        foreach(var item in items){item.gameObject.SetActive(false);CharacterManager.DestroyRuntimeObject(item.gameObject);}items.Clear();
        filtered=catalog.Entries.Where(e=>e.Category==category).ToList();
        var entries=filtered.ToArray();
        for(int i=0;i<entries.Length;i++){
            if(request!=categoryTicket||!isActiveAndEnabled)yield break;
            var chosen=entries[i];var button=Instantiate(itemTemplate,content,false);button.name=chosen.Resource;button.GetComponentInChildren<Text>(true).text=chosen.Label;
            button.gameObject.SetActive(true);button.onClick.AddListener(()=>Select(chosen));items.Add(button);
            Loading.UpdateProgress((i+1)*100/Math.Max(1,entries.Length));
            if(i%24==23)yield return null;
        }
        Canvas.ForceUpdateCanvases();var scroll=content.GetComponentInParent<ScrollRect>();if(scroll!=null)scroll.verticalNormalizedPosition=1;
        UpdateButtons();
    }
    internal void Select(HissatsuCatalog.Entry entry)
    {
        playing=false;ClearImportedPreviews();settings=null;
        Select(entry,false);
    }
    public void SelectAnimation(bool alternate)
    {
        if(selected==null||alternate==alternateAnimation||alternate&&string.IsNullOrEmpty(selected.AlternateEvent))return;
        Select(selected,alternate);
    }
    void Select(HissatsuCatalog.Entry entry,bool alternate)
    {
        playing=false;
        int request=++ticket;
        StartCoroutine(QueueSelection(entry,alternate,request));
    }
    IEnumerator QueueSelection(HissatsuCatalog.Entry entry,bool alternate,int request)
    {
        while(Loading.IsRunning){if(request!=ticket||!isActiveAndEnabled)yield break;yield return null;}
        if(request==ticket&&isActiveAndEnabled)Loading.Run(()=>LoadSelection(entry,alternate,request),()=>{ticket++;});
    }
    IEnumerator LoadSelection(HissatsuCatalog.Entry entry,bool alternate,int request)
    {
        selectionLoading=true;playing=false;
        progress.interactable=playButton.interactable=false;
        HissatsuPreview pending=null;bool stored=false;
        string eventName=alternate?entry.AlternateEvent:entry.Event;
        string cacheKey=entry.Id.ToString("X8")+"|"+eventName;
        try{
            if(!cache.TryGetValue(cacheKey,out var next)){
                Loading.UpdateText(entry.Label);yield return null;
                pending=next=new HissatsuPreview(HissatsuEvent.Read(eventName),modelParent,previewCamera,entry.AssignedModel,entry.AssignedPath,entry.Category==3||(entry.Category==2&&alternate));
                var loader=pending.Load(()=>request!=ticket||this==null||!isActiveAndEnabled);
                try{while(loader.MoveNext()){if(request!=ticket||!isActiveAndEnabled)yield break;yield return loader.Current;}}
                finally{(loader as IDisposable)?.Dispose();}
                if(request!=ticket||!isActiveAndEnabled)yield break;
                cache.Add(cacheKey,next);stored=true;
            }
            if(request!=ticket)yield break;
            if(active!=null)active.Root.SetActive(false);active=next;lastLoggedCut=null;selected=entry;alternateAnimation=alternate;position=0;playing=false;dragging=false;resumeAfterDrag=false;
            PrepareEditing(entry);yield return EnsureImportedPreviews(Sequence,request);if(request!=ticket)yield break;EvaluateActive(0);playing=PlaybackDuration>0;UpdateTarget();recent.Remove(cacheKey);recent.Add(cacheKey);
            while(recent.Count>3){string key=recent[0];recent.RemoveAt(0);cache[key].Dispose();cache.Remove(key);}
            foreach(string warning in active.Warnings.Distinct())AppLog.Warning("[Hissatsu] "+entry.Resource+" / "+warning);
            progress.interactable=playButton.interactable=PlaybackDuration>0;UpdateButtons();UpdateControls();
        }finally{selectionLoading=false;if(pending!=null&&!stored)pending.Dispose();}
    }
    void Update()
    {
        if(catalog!=null&&locale!=UniformCatalog.Locale){catalog=new HissatsuCatalog();locale=UniformCatalog.Locale;SelectCategory(category);}
        if(!focused||!isActiveAndEnabled)return;
        UpdateTarget();
        if(selectionLoading||active==null||!playing||dragging)return;
        position=Mathf.Min(PlaybackDuration,position+Time.unscaledDeltaTime);
        try{EvaluateActive(position);}catch(Exception e){playing=false;AppLog.Exception(e,this);}
        if(position>=PlaybackDuration)playing=false;UpdateControls();
    }
    void EvaluateActive(float seconds)
    {
        var sequence=Sequence;int index=sequence?.IndexAt(seconds)??-1;
        var preview=active;
        if(sequence!=null){
            if(index<0){ShowPlaybackPreview(null);return;}
            var clip=sequence.clips[index];
            if(!string.IsNullOrEmpty(clip.sourceEvent)&&!importedPreviews.TryGetValue(ClipPreviewKey(clip),out preview)){
                playing=false;ShowPlaybackPreview(null);
                if(selectionLoading)return;
                throw new InvalidOperationException("Motion preview has not loaded: "+clip.sourceEvent+" / "+clip.motion);
            }
            seconds=sequence.SourceTime(seconds);
        }
        ShowPlaybackPreview(preview);preview.Evaluate(seconds);
        string cut=preview.Event.Name+"/"+preview.Event.At(seconds).Name;
        if(lastLoggedCut!=cut){lastLoggedCut=cut;AppLog.Info(preview.DescribeCharacters(seconds),this);}
        if(stadium?.Root!=null){
            bool visible=preview.Event.SceneVisibleAt(seconds);
            if(stadium.Root.activeSelf!=visible)stadium.Root.SetActive(visible);
        }
    }
    void UpdateButtons()
    {
        UpdateAnimationButtons();
        for(int i=0;i<categories.Count;i++)categories[i].interactable=i!=category;
        for(int i=0;i<items.Count;i++)items[i].interactable=settings?.isNew==true||selected?.Id!=filtered[i].Id;
    }
    void UpdateAnimationButtons()
    {
        bool available=selected!=null&&selected.Category==category&&!string.IsNullOrEmpty(selected.AlternateEvent);
        if(animationVariantParent!=null)animationVariantParent.SetActive(available);
        if(normalAnimationButton!=null)normalAnimationButton.interactable=available&&alternateAnimation;
        if(alternateAnimationButton!=null)alternateAnimationButton.interactable=available&&!alternateAnimation;
        if(alternateAnimationText!=null)alternateAnimationText.text=LocalizationManager.GetText(selected?.Category==2?"HissatsuShotBlockAnimation":"HissatsuFailedCatchAnimation",gameObject.scene.name);
    }
    public void TogglePlay(){if(selectionLoading||active==null||PlaybackDuration<=0)return;if(position>=PlaybackDuration)position=0;playing=!playing;EvaluateActive(position);if(!playing)AppLog.Info((renderedPreview??active).DescribeCharacters(CurrentSourceTime),this);UpdateControls();}
    public void BeginScrub(){if(selectionLoading)return;resumeAfterDrag=playing;dragging=true;playing=false;UpdateControls();}
    public void EndScrub(){if(selectionLoading)return;if(active!=null)AppLog.Info((renderedPreview??active).DescribeCharacters(CurrentSourceTime),this);dragging=false;playing=resumeAfterDrag&&active!=null&&position<PlaybackDuration;UpdateControls();}
    public void Seek(float value)
    {
        if(selectionLoading||active==null)return;position=value*PlaybackDuration;
        try{EvaluateActive(position);}catch(Exception e){playing=false;AppLog.Exception(e,this);}UpdateControls();
    }
    void UpdateControls()
    {
        progress.SetValueWithoutNotify(active==null?0:position/Mathf.Max(.001f,PlaybackDuration));
        timeText.text=$"{position:0.00} / {PlaybackDuration:0.00} s";
        if(playText!=null)playText.text=LocalizationManager.GetText(playing?"HissatsuPause":"HissatsuPlay");
        if(playIcon!=null)playIcon.SetActive(!playing);
        if(pauseIcon!=null)pauseIcon.SetActive(playing);
    }
    void UpdateTarget()
    {
        if(display==null||previewCamera==null)return;
        if(displayMaterial==null){
            var shader=Resources.Load<Shader>("HissatsuPreviewDisplay");
            if(shader==null)throw new InvalidOperationException("Missing HissatsuPreviewDisplay shader");
            previousDisplayMaterial=display.material;
            displayMaterial=new Material(shader){name="Hissatsu completed frame"};display.material=displayMaterial;
        }
        var fitter=display.GetComponent<AspectRatioFitter>();if(fitter!=null)fitter.aspectRatio=16f/9;
        int width=Mathf.Clamp(Mathf.RoundToInt(display.rectTransform.rect.width*display.canvas.scaleFactor),320,1920),height=Mathf.RoundToInt(width*9f/16f);
        if(texture==null||texture.width!=width){if(texture!=null){texture.Release();Destroy(texture);}texture=new RenderTexture(width,height,24){name="Hissatsu preview",filterMode=FilterMode.Bilinear};}
        if(!texture.IsCreated())texture.Create();display.texture=texture;previewCamera.targetTexture=texture;previewCamera.aspect=16f/9;previewCamera.enabled=focused&&active!=null;
    }
    void OnApplicationFocus(bool value){focused=value;if(previewCamera!=null)previewCamera.enabled=value&&isActiveAndEnabled&&active!=null;}
    void OnApplicationPause(bool value){OnApplicationFocus(!value);}
    void OnDisable(){ticket++;categoryTicket++;if(clipSkillPicker!=null)clipSkillPicker.Close();if(previewCamera!=null)previewCamera.enabled=false;}
    void OnDestroy(){ClearImportedPreviews();if(displayMaterial!=null){if(display!=null)display.material=previousDisplayMaterial;Destroy(displayMaterial);}ticket++;foreach(var entry in cache.Values)entry.Dispose();cache.Clear();stadium?.Dispose();if(previewCamera!=null)previewCamera.targetTexture=null;if(texture!=null){texture.Release();Destroy(texture);}}
}
