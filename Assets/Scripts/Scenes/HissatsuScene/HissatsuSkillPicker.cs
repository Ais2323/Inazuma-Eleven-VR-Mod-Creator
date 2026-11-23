using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

// Scene-authored category/search/page layout shared visually with CharacterMaker.
public sealed class HissatsuSkillPicker : MonoBehaviour
{
    public Transform categoryParent,itemsParent;
    public InputField searchInput,pageInput;
    public Button previousPage,nextPage,closeButton;
    public Sprite[] affinitySprites;
    readonly List<Button> categories=new(),items=new();
    List<HissatsuCatalog.Entry> choices,filtered;
    HissatsuCatalog.Entry selected;
    Action<HissatsuCatalog.Entry> chosen;
    Func<bool> current;
    int category,page;
    bool initialized;
    void Initialize()
    {
        if(initialized)return;initialized=true;
        foreach(Transform child in categoryParent){
            var button=child.GetComponent<Button>();if(button==null)continue;
            int index=categories.Count;categories.Add(button);button.onClick.AddListener(()=>SelectCategory(index));
        }
        foreach(Transform child in itemsParent){
            var button=child.GetComponent<Button>();if(button==null)continue;
            int index=items.Count;items.Add(button);button.onClick.AddListener(()=>SelectItem(index));
        }
        searchInput.onValueChanged.AddListener(_=>{page=0;Refresh();});
        previousPage.onClick.AddListener(()=>{page--;Refresh();});
        nextPage.onClick.AddListener(()=>{page++;Refresh();});
        pageInput.contentType=InputField.ContentType.IntegerNumber;
        pageInput.onEndEdit.AddListener(value=>{if(int.TryParse(value,out int number))page=number-1;Refresh();});
        closeButton.onClick.AddListener(Close);
    }
    internal void Open(IEnumerable<HissatsuCatalog.Entry> entries,int initialCategory,HissatsuCatalog.Entry selection,
        Func<bool> isCurrent,Action<HissatsuCatalog.Entry> onChosen)
    {
        Initialize();choices=entries.ToList();selected=selection;current=isCurrent;chosen=onChosen;
        gameObject.SetActive(true);transform.SetAsLastSibling();searchInput.SetTextWithoutNotify("");
        SelectCategory(Mathf.Clamp(initialCategory,0,categories.Count-1));
    }
    void SelectCategory(int index){category=index;page=0;Refresh();}
    void Refresh()
    {
        string search=searchInput.text.Trim();
        filtered=choices.Where(e=>e.Category==category&&(search.Length==0||e.Label.IndexOf(search,StringComparison.CurrentCultureIgnoreCase)>=0||
            e.Resource.IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0||e.Id.ToString("X8").IndexOf(search,StringComparison.OrdinalIgnoreCase)>=0)).ToList();
        int count=Math.Max(1,(filtered.Count+items.Count-1)/items.Count);page=Mathf.Clamp(page,0,count-1);
        for(int i=0;i<categories.Count;i++)categories[i].interactable=i!=category;
        for(int i=0;i<items.Count;i++){
            int at=page*items.Count+i;var button=items[i];button.gameObject.SetActive(at<filtered.Count);
            if(at>=filtered.Count)continue;var entry=filtered[at];
            button.GetComponentInChildren<Text>(true).text=entry.Label+" / "+entry.Resource;
            button.interactable=selected==null||entry.Id!=selected.Id||entry.Resource!=selected.Resource;
            var image=button.transform.Find("Affinity")?.GetComponent<Image>();
            if(image!=null){image.sprite=entry.Affinity>=0&&entry.Affinity<affinitySprites.Length?affinitySprites[entry.Affinity]:null;image.enabled=image.sprite!=null;}
        }
        previousPage.interactable=page>0;nextPage.interactable=page+1<count;
        pageInput.SetTextWithoutNotify((page+1).ToString(CultureInfo.InvariantCulture));
    }
    void SelectItem(int index)
    {
        int at=page*items.Count+index;
        if(current?.Invoke()!=true||at<0||at>=filtered.Count){Close();return;}
        var entry=filtered[at];var callback=chosen;Close();callback?.Invoke(entry);
    }
    public void Close(){gameObject.SetActive(false);chosen=null;current=null;}
    void Update(){if(current?.Invoke()==false||UnityEngine.InputSystem.Keyboard.current?.escapeKey.wasPressedThisFrame==true)Close();}
    void OnDisable(){chosen=null;current=null;}
}
