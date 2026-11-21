using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;

public sealed class HissatsuSceneController : MonoBehaviour
{
    public Transform categoryParent,content,modelParent;
    public Button itemTemplate,playButton;
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
    bool playing,dragging,resumeAfterDrag,focused=true;
    RenderTexture texture;
    string locale;
    IEnumerator Start()=>Loading.Track(Initialize(),"LoadingInitialize");
    IEnumerator Initialize()
    {
        foreach(Transform child in categoryParent){var button=child.GetComponent<Button>();if(button==null)continue;int at=categories.Count;categories.Add(button);button.onClick.AddListener(()=>SelectCategory(at));}
        playButton.onClick.AddListener(TogglePlay);progress.onValueChanged.AddListener(Seek);
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
        catalog=new HissatsuCatalog();locale=UniformCatalog.Locale;yield return PopulateCategory(0);UpdateControls();
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
        int request=++ticket;
        StartCoroutine(QueueSelection(entry,request));
    }
    IEnumerator QueueSelection(HissatsuCatalog.Entry entry,int request)
    {
        while(Loading.IsRunning){if(request!=ticket||!isActiveAndEnabled)yield break;yield return null;}
        if(request==ticket&&isActiveAndEnabled)Loading.Run(()=>LoadSelection(entry,request),()=>{ticket++;});
    }
    IEnumerator LoadSelection(HissatsuCatalog.Entry entry,int request)
    {
        HissatsuPreview pending=null;bool stored=false;
        string cacheKey=entry.Id.ToString("X8");
        try{
            if(!cache.TryGetValue(cacheKey,out var next)){
                Loading.UpdateText(entry.Label);yield return null;
                pending=next=new HissatsuPreview(HissatsuEvent.Read(entry.Event),modelParent,previewCamera,entry.AssignedModel,entry.AssignedPath);
                var loader=pending.Load(()=>request!=ticket||this==null||!isActiveAndEnabled);
                try{while(loader.MoveNext()){if(request!=ticket||!isActiveAndEnabled)yield break;yield return loader.Current;}}
                finally{(loader as IDisposable)?.Dispose();}
                if(request!=ticket||!isActiveAndEnabled)yield break;
                cache.Add(cacheKey,next);stored=true;
            }
            if(request!=ticket)yield break;
            if(active!=null)active.Root.SetActive(false);active=next;selected=entry;position=0;playing=true;dragging=false;resumeAfterDrag=false;
            active.Root.SetActive(true);active.Evaluate(0);UpdateTarget();recent.Remove(cacheKey);recent.Add(cacheKey);
            while(recent.Count>3){string key=recent[0];recent.RemoveAt(0);cache[key].Dispose();cache.Remove(key);}
            if(stored)foreach(string warning in active.Warnings.Distinct())AppLog.Warning("[Hissatsu] "+entry.Resource+" / "+warning);
            progress.interactable=playButton.interactable=true;UpdateButtons();UpdateControls();
        }finally{if(pending!=null&&!stored)pending.Dispose();}
    }
    void Update()
    {
        if(catalog!=null&&locale!=UniformCatalog.Locale){catalog=new HissatsuCatalog();locale=UniformCatalog.Locale;SelectCategory(category);}
        if(!focused||!isActiveAndEnabled)return;
        UpdateTarget();
        if(active==null||!playing||dragging)return;
        position=Mathf.Min(active.Duration,position+Time.unscaledDeltaTime);
        try{active.Evaluate(position);}catch(Exception e){playing=false;AppLog.Exception(e,this);}
        if(position>=active.Duration)playing=false;UpdateControls();
    }
    void UpdateButtons()
    {
        for(int i=0;i<categories.Count;i++)categories[i].interactable=i!=category;
        for(int i=0;i<items.Count;i++)items[i].interactable=selected?.Id!=filtered[i].Id;
    }
    public void TogglePlay(){if(active==null)return;if(position>=active.Duration)position=0;playing=!playing;active.Evaluate(position);UpdateControls();}
    public void BeginScrub(){resumeAfterDrag=playing;dragging=true;playing=false;UpdateControls();}
    public void EndScrub(){dragging=false;playing=resumeAfterDrag&&active!=null&&position<active.Duration;UpdateControls();}
    public void Seek(float value)
    {
        if(active==null)return;position=value*active.Duration;
        try{active.Evaluate(position);}catch(Exception e){playing=false;AppLog.Exception(e,this);}UpdateControls();
    }
    void UpdateControls()
    {
        progress.SetValueWithoutNotify(active==null?0:position/Mathf.Max(.001f,active.Duration));
        timeText.text=$"{position:0.00} / {(active?.Duration??0):0.00} s";
        playText.text=LocalizationManager.GetText(playing?"HissatsuPause":"HissatsuPlay");
    }
    void UpdateTarget()
    {
        if(display==null||previewCamera==null)return;
        var fitter=display.GetComponent<AspectRatioFitter>();if(fitter!=null)fitter.aspectRatio=16f/9;
        int width=Mathf.Clamp(Mathf.RoundToInt(display.rectTransform.rect.width*display.canvas.scaleFactor),320,1920),height=Mathf.RoundToInt(width*9f/16f);
        if(texture==null||texture.width!=width){if(texture!=null){texture.Release();Destroy(texture);}texture=new RenderTexture(width,height,24){name="Hissatsu preview",filterMode=FilterMode.Bilinear};}
        if(!texture.IsCreated())texture.Create();display.texture=texture;previewCamera.targetTexture=texture;previewCamera.aspect=16f/9;previewCamera.enabled=focused&&active!=null;
    }
    void OnApplicationFocus(bool value){focused=value;if(previewCamera!=null)previewCamera.enabled=value&&isActiveAndEnabled&&active!=null;}
    void OnApplicationPause(bool value){OnApplicationFocus(!value);}
    void OnDisable(){ticket++;categoryTicket++;if(previewCamera!=null)previewCamera.enabled=false;}
    void OnDestroy(){ticket++;foreach(var entry in cache.Values)entry.Dispose();cache.Clear();stadium?.Dispose();if(previewCamera!=null)previewCamera.targetTexture=null;if(texture!=null){texture.Release();Destroy(texture);}}
}
