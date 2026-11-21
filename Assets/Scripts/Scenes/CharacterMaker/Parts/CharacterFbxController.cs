using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using G4Composition;
using UnityEngine;
using UnityEngine.UI;
using SFB;

// Scene-authored controls; only mesh/bone rows are generated from the FBX.
public sealed class CharacterFbxController : MonoBehaviour
{
    public CharacterManager manager;
    public GameObject panel;
    public Transform meshContent,boneContent;
    public GameObject meshTemplate,boneTemplate;
    public InputField scaleInput;
    public Toggle flipVToggle;
    public Text status;
    public Button previewButton,applyButton,cancelButton;
    public Button importCompleteButton,exportCompleteButton;
    readonly List<GameObject> rows=new();
    readonly Dictionary<int,CharacterMakerPart> pendingParts=new();
    int[] targetOptions;
    UniformFbxModel settings;
    int option;
    int[] choices;
    G4Runtime.G4ModelParser.Part[] targets;
    GameObject preview;
    readonly List<GameObject> hidden=new();
    bool initialized;
    void Awake(){Initialize();}
    void Initialize() {
        if(initialized)return; initialized=true;
        previewButton.onClick.AddListener(Preview);applyButton.onClick.AddListener(Apply);cancelButton.onClick.AddListener(Close);
        if(importCompleteButton!=null)importCompleteButton.onClick.AddListener(()=>Open(0));
        if(exportCompleteButton!=null)exportCompleteButton.onClick.AddListener(()=>Export(0));
        scaleInput.onValueChanged.AddListener(s=>{if(settings==null)return;settings.scale=float.TryParse(s,NumberStyles.Float,CultureInfo.InvariantCulture,out float n)?n:0;Refresh();});
        flipVToggle.onValueChanged.AddListener(v=>{if(settings!=null)settings.flipV=v;});
    }
    internal void RefreshAvailability() {
        foreach(var page in new CharacterPartPageController[]{manager.headPage,manager.hairPage,manager.otherPage}) {
            if(page.importFbxButton!=null)page.importFbxButton.interactable=true;
            if(page.exportFbxButton!=null)page.exportFbxButton.interactable=true;
        }
        if(importCompleteButton!=null)importCompleteButton.interactable=true;
        if(exportCompleteButton!=null)exportCompleteButton.interactable=true;
    }
    public void Open(int partOption)=>Loading.Perform(()=>{
        try {
            var paths=StandaloneFileBrowser.OpenFilePanel(LocalizationManager.GetText("CharacterFbxImport"),"","fbx",false);
            if(paths!=null&&paths.Length>0)OpenFile(paths[0],partOption);
        }catch(Exception e){AppLog.Error("[Character FBX] "+e.Message,this);}
    },"LoadingFbx");
    internal void OpenFile(string path,int partOption) {
        Initialize();Close();
        var part=manager.FbxImportPart(partOption==0?1:partOption);
        var native=CharacterFbxAsset.Read(part,false);
        if(new FileInfo(path).Length>64*1024*1024)throw new InvalidDataException("FBX exceeds 64 MB.");
        byte[] bytes=File.ReadAllBytes(path);var reader=UniformFbxReader.Read(bytes);var rig=CharacterFbxAsset.Rig(native);
        var bound=reader.BindAutomatically(bytes,Path.GetFileName(path),rig);
        option=partOption;settings=bound;
        var slots=new List<G4Runtime.G4ModelParser.Part>();var owners=new List<int>();
        foreach(int owner in partOption==0?new[]{1,2,3}:new[]{partOption}) {
            var item=manager.FbxImportPart(owner);pendingParts[owner]=item;
            var itemAsset=CharacterFbxAsset.Read(item,false);
            item.meshNames=CharacterFbxAsset.NativeNames(item,itemAsset,owner);
            var names=new HashSet<string>(item.meshNames,StringComparer.OrdinalIgnoreCase);
            foreach(var slot in itemAsset.Preview.Parts.Where(p=>names.Contains(p.Name))){slots.Add(slot);owners.Add(owner);}
        }
        targets=slots.ToArray();targetOptions=owners.ToArray();
        if(targets.Length==0)throw new InvalidDataException("No native mesh slots selected.");
        choices=new int[reader.Parts.Length];
        var meshOptions=new List<string>{LocalizationManager.GetText("CharacterFbxSkip")};
        meshOptions.AddRange(targets.Select((p,i)=>PartLabel(targetOptions[i])+" / "+p.Name));
        for(int i=0;i<reader.Parts.Length;i++) {
            int index=i;var mesh=reader.Parts[i];
            int match=Array.FindIndex(targets,p=>p.Name.Equals(mesh.Name,StringComparison.OrdinalIgnoreCase));
            for(int t=0;t<targets.Length;t++)if(mesh.Name.Equals(ExportPrefix(targetOptions[t])+targets[t].Name,StringComparison.OrdinalIgnoreCase))match=t;
            if(match<0)foreach(int owner in pendingParts.Keys) {
                var owned=Enumerable.Range(0,targets.Length).Where(t=>targetOptions[t]==owner).ToArray();
                if(owned.Length==1&&mesh.Name.StartsWith(ExportPrefix(owner),StringComparison.OrdinalIgnoreCase))match=owned[0];
            }
            choices[i]=match>=0?match:targets.Length==1?0:-1;
            AddRow(meshTemplate,meshContent,mesh.Name,meshOptions,choices[i]+1,v=>{choices[index]=v-1;Refresh();});
        }
        var boneOptions=new List<string>{LocalizationUnassigned()};boneOptions.AddRange(rig.Names);
        for(int i=0;i<reader.Bones.Length;i++) {
            int index=i;int at=rig.ByHash.TryGetValue(bound.bones[i].targetHash,out int bone)?bone+1:0;
            AddRow(boneTemplate,boneContent,reader.Bones[i].Name,boneOptions,at,v=>{bound.bones[index].targetHash=v==0?0:rig.Hashes[v-1];Refresh();});
        }
        scaleInput.SetTextWithoutNotify(bound.scale.ToString(CultureInfo.InvariantCulture));flipVToggle.SetIsOnWithoutNotify(bound.flipV);
        panel.SetActive(true);Refresh();
    }
    static string LocalizationUnassigned()=>LocalizationManager.GetText("CharacterFbxUnassigned");
    void AddRow(GameObject template,Transform parent,string name,List<string> options,int value,Action<int> changed) {
        var row=Instantiate(template,parent,false);rows.Add(row);row.SetActive(true);
        row.transform.Find("SourceName").GetComponent<Text>().text=name;
        var dropdown=row.transform.Find("Target").GetComponent<Dropdown>();dropdown.ClearOptions();dropdown.AddOptions(options);dropdown.SetValueWithoutNotify(value);dropdown.onValueChanged.AddListener(v=>changed(v));
    }
    void Refresh() {
        if(settings==null)return;
        int mapped=settings.bones.Count(b=>b.targetHash!=0);
        status.text=settings.fileName+" — "+string.Format(LocalizationManager.GetText("CharacterFbxMapped"),mapped,settings.bones.Count);
        bool valid=mapped==settings.bones.Count&&float.IsFinite(settings.scale)&&settings.scale>0&&choices.Any(c=>c>=0);
        previewButton.interactable=applyButton.interactable=valid;
    }
    internal static string ExportPrefix(int owner)=>owner==1?"Head__":owner==2?"Hair__":"Other__";
    static string PartLabel(int owner)=>LocalizationManager.GetText(owner==1?"Head":owner==2?"Hair":"Other");
    Dictionary<int,CharacterMakerPart> BoundParts() {
        var result=new Dictionary<int,CharacterMakerPart>();
        foreach(var entry in pendingParts) {
            int owner=entry.Key;
            var indices=Enumerable.Range(0,choices.Length).Where(i=>choices[i]>=0&&targetOptions[choices[i]]==owner).ToArray();
            if(indices.Length==0)continue;
            var bound=settings.Clone();bound.meshIndices=indices;
            bound.meshSlots=indices.Select(i=>targets[choices[i]].SourceMeshIndex).ToArray();bound.Validate();
            var part=JsonUtility.FromJson<CharacterMakerPart>(JsonUtility.ToJson(entry.Value));part.fbx=bound;
            if(option==0){part.position=Vector3.zero;part.rotation=Vector3.zero;part.scale=Vector3.one;}
            result[owner]=part;
        }
        return result;
    }
    public void Preview()=>Loading.Perform(()=>{
        try { var parts=BoundParts();var assets=parts.ToDictionary(e=>e.Key,e=>CharacterFbxAsset.Read(e.Value));ReleasePreview();
            preview=new GameObject("CompleteHeadFbxPreview");preview.transform.SetParent(manager.nowCharacterData.nowShowParent,false);
            foreach(var entry in parts){var model=manager.FbxPreview(entry.Key,entry.Value,assets[entry.Key]);model.transform.SetParent(preview.transform,true);
                foreach(var tr in manager.FbxSelected(entry.Key))if(tr!=null&&tr.gameObject.activeSelf){hidden.Add(tr.gameObject);tr.gameObject.SetActive(false);}}
        }catch(Exception e){ReleasePreview();AppLog.Error("[Character FBX] "+e.Message,this);}
    },"LoadingModel");
    public void Apply()=>Loading.Perform(()=>{
        try {var parts=BoundParts();foreach(var part in parts.Values)CharacterFbxAsset.Read(part);
            ReleasePreview();foreach(var entry in parts)manager.ApplyFbx(entry.Key,entry.Value);Close();AppLog.Result(LocalizationManager.GetText("CharacterFbxApplied"),this);}
        catch(Exception e){AppLog.Error("[Character FBX] "+e.Message,this);}
    },"LoadingModel");
    void ReleasePreview(){if(preview!=null){preview.SetActive(false);CharacterManager.DestroyRuntimeObject(preview);preview=null;}foreach(var go in hidden)if(go!=null)go.SetActive(true);hidden.Clear();}
    public void Close(){ReleasePreview();foreach(var row in rows)if(row!=null){row.SetActive(false);CharacterManager.DestroyRuntimeObject(row);}rows.Clear();pendingParts.Clear();settings=null;if(panel!=null)panel.SetActive(false);}
    void OnDisable(){Close();}
    void OnDestroy(){ReleasePreview();}

    public void Export(int partOption)=>Loading.Perform(()=>{
        try {
            var asset=partOption==0?manager.FbxCompleteHead():null;
            var part=partOption==0?new CharacterMakerPart{sourceId=manager.nowCharacterData.TemplateId,meshNames=asset.Preview.Parts.Select(p=>p.Name).ToList()}:manager.FbxExportPart(partOption);
            if(string.IsNullOrEmpty(part.sourceId))throw new InvalidDataException(LocalizationManager.GetText("CharacterFbxNoPart"));
            asset??=CharacterFbxAsset.Read(part);
            if(partOption!=0)part.meshNames=CharacterFbxAsset.NativeNames(part,asset,partOption);
            string path=StandaloneFileBrowser.SaveFilePanel(LocalizationManager.GetText("CharacterFbxExport"),SettingManager.GetExportDirectory("characterFbxExportDirectory"),part.sourceId+"_"+partOption,"fbx");
            if(string.IsNullOrEmpty(path))return;if(!path.EndsWith(".fbx",StringComparison.OrdinalIgnoreCase))path+=".fbx";
            WriteFbx(path,partOption,part,asset);SettingManager.RememberExportDirectory("characterFbxExportDirectory",Path.GetDirectoryName(Path.GetFullPath(path)));
            AppLog.Result(LocalizationManager.GetText("CharacterFbxExported")+": "+path,this);
        }catch(Exception e){AppLog.Error("[Character FBX] "+e.Message,this);}
    },"LoadingExport");
    internal void WriteFbx(string path,int partOption,CharacterMakerPart part,RawHeadAsset asset) {
        string folder=Path.GetFileNameWithoutExtension(path)+"_textures";
        var files=new Dictionary<string,byte[]>();var textureNames=new Dictionary<Texture,string>();
        GameObject completePreview=partOption==0||!manager.FbxSelected(partOption).Any(t=>t!=null)?CharacterFbxAsset.Preview(asset,part.sourceId):null;
        if(completePreview!=null)completePreview.SetActive(false);
        var selected=(completePreview!=null?completePreview.transform.Cast<Transform>():manager.FbxSelected(partOption).Where(t=>t!=null)).ToDictionary(t=>t.name,StringComparer.OrdinalIgnoreCase);
        string TexturePath(Material material,string property) {
            var texture=material!=null&&material.HasProperty(property)?material.GetTexture(property):null;if(texture==null)return null;
            if(textureNames.TryGetValue(texture,out string known))return known;
            byte[] png=UniformTextureCodec.ToPng(texture,property!="_MainTex");string name=folder+"/"+RawHeadAsset.Digest(png)+".png";
            files[name]=png;textureNames[texture]=name;return name;
        }
        try {
        var scene=CharacterFbxAsset.Capture(asset,new HashSet<string>(part.meshNames,StringComparer.OrdinalIgnoreCase),slot=>{
            string name=asset.Preview.Parts.Single(p=>p.SourceMeshIndex==slot).Name;
            return selected.TryGetValue(name,out var tr)?tr.GetComponent<Renderer>()?.sharedMaterial:null;
        },TexturePath);
        if(partOption==0) {
            // Native composition adopts the base model's slot names. Reattach the
            // UI source names by their semantic slots so a full FBX maps back to
            // mixed-source hair/accessories rather than inheriting the base names.
            int nonFace=0;
            foreach(var mesh in scene.Meshes) {
                int owner=mesh.Name.StartsWith("eye_",StringComparison.OrdinalIgnoreCase)||mesh.Name.StartsWith("mouth_",StringComparison.OrdinalIgnoreCase)?1:++nonFace==1?2:3;
                var originals=manager.FbxSelected(owner);
                string sourceName=owner==1?originals.FirstOrDefault(t=>t!=null&&t.name.StartsWith(mesh.Name.StartsWith("eye_",StringComparison.OrdinalIgnoreCase)?"eye_":"mouth_",StringComparison.OrdinalIgnoreCase))?.name:originals.FirstOrDefault(t=>t!=null)?.name;
                mesh.Name=ExportPrefix(owner)+(sourceName??mesh.Name);
            }
        }
        byte[] bytes=BinaryFbxExporter.Encode(scene);var check=UniformFbxReader.Read(bytes);
        if(check.Parts.Length!=scene.Meshes.Count||check.Bones.Length==0)throw new InvalidDataException("FBX export readback failed.");
        string directory=Path.GetDirectoryName(Path.GetFullPath(path));Directory.CreateDirectory(directory);
        foreach(var file in files){string output=Path.Combine(directory,file.Key);Directory.CreateDirectory(Path.GetDirectoryName(output));File.WriteAllBytes(output,file.Value);}
        File.WriteAllBytes(path,bytes);
        }finally{if(completePreview!=null)CharacterManager.DestroyRuntimeObject(completePreview);}
    }
}
