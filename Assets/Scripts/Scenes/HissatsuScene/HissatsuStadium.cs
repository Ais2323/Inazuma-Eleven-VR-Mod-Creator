using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;
using UnityEngine;

// One shared native outdoor stadium, independent of the cached move instances.
internal sealed class HissatsuStadium : IDisposable
{
    internal GameObject Root { get; private set; }
    internal IEnumerator Load(Transform parent)
    {
        const string stem="s01g001";
        string relative=Path.Combine("map","s",stem);
        string directory=Path.Combine(DumpDataManager.CommonPath,relative);
        Root=new GameObject("Stadium_"+stem);Root.transform.SetParent(parent,false);
        // Soccer events run along Z; the source stadium's pitch runs along X.
        Root.transform.localRotation=Quaternion.Euler(0,90,0);
        var resources=Root.AddComponent<RuntimeHeadResources>();
        var textures=new Dictionary<uint,Texture2D>();
        var data=new Dictionary<uint,G4TextureParser.TextureData>();
        var containers=new Queue<string>(new[]{stem+"g",stem+"g50",stem+"f",stem+"vr",stem+"i05",stem+"i",stem}.Select(n=>Path.Combine(DumpDataManager.Dx11Path,relative,n+".g4tx")).Concat(new[]{Path.Combine(DumpDataManager.Dx11Path,"map","_default","texture.g4tx")}));
        Texture2D Texture(uint hash){
            if(textures.TryGetValue(hash,out var ready))return ready;
            while(!data.ContainsKey(hash)&&containers.Count>0){
                string path=containers.Dequeue();if(!File.Exists(path))continue;
                var warnings=new List<string>();foreach(var t in G4TextureParser.Parse(File.ReadAllBytes(path),path,warnings,baseOnly:false))data[t.Hash]=t;
                foreach(string warning in warnings)if(!(warning.Contains("_cb")&&warning.Contains("only 2D DDS")))AppLog.Warning("[Hissatsu stadium] "+warning);
            }
            if(!data.TryGetValue(hash,out var source))throw new InvalidDataException("Stadium texture not found: "+hash.ToString("X8"));
            var texture=resources.Own(new Texture2D(source.Width,source.Height,source.Format,source.MipCount,false){name=source.Name,wrapMode=TextureWrapMode.Repeat});
            texture.LoadRawTextureData(source.Pixels);texture.Apply(false,true);textures.Add(hash,texture);return texture;
        }
        string[] models=new[]{stem+"g50",stem+"roof"};
        for(int i=0;i<models.Length;i++){
            string name=models[i],folder=Path.Combine(relative,name),path=Path.Combine(directory,name,name+".g4pkm");
            Loading.UpdateText(LocalizationManager.GetText("HissatsuStadiumLoading")+" / "+name);yield return null;
            var blocks=G4AnimationBank.Package(File.ReadAllBytes(path),path);byte[] md=blocks["G4MD"];
            var parsed=G4ModelParser.Parse(md,File.ReadAllBytes(Path.ChangeExtension(path,".g4mg")),path);
            // Use the group-local rig for screens, flags and mesh nodes.
            // The traversal map parks alternate stadium groups thousands of
            // units apart; a soccer preview places the chosen group at origin.
            var modelRig=G4Skeleton.Read(blocks["G4SK"],path,"Stadium model bind pose");
            var model=G4ModelLoader.CreateModel(folder,name,loadTextures:false,skeletonOverride:modelRig,headLocalSkinning:false,metadataOverride:md);
            model.transform.SetParent(Root.transform,false);
            var b=new G4ModelParser.Binary(md,path);int bias=b.U16(10)*4;int Section(int field)=>bias+b.U16(field)*4;
            foreach(var renderer in model.GetComponentsInChildren<Renderer>()){
                // Native culling volumes and crowd alternatives are not scenery.
                if(renderer.name.StartsWith("__")||renderer.name.IndexOf("culling",StringComparison.OrdinalIgnoreCase)>=0||new[]{"keizen","nagumo","hokuyo","raimon","teikoku"}.Any(n=>renderer.name.IndexOf(n,StringComparison.OrdinalIgnoreCase)>=0)){renderer.enabled=false;continue;}
                var material=renderer.sharedMaterial;int at=parsed.Materials.FindIndex(m=>m.Name==material.name);if(at<0)continue;
                // Reflection probe proxy volumes are not visible stadium meshes.
                if(material.name.IndexOf("_cb_",StringComparison.OrdinalIgnoreCase)>=0){renderer.enabled=false;continue;}
                int record=Section(0x64)+at*16;
                material.shader=Resources.Load<Shader>("G4StadiumPreview")??throw new InvalidDataException("Missing stadium shader");
                if(parsed.Materials[at].TextureHashes.Count>0)material.mainTexture=Texture(parsed.Materials[at].TextureHashes[0]);
                bool grass=material.name.StartsWith("Map_ParallaxGrass",StringComparison.OrdinalIgnoreCase);
                material.SetFloat("_Grass",grass?1:0);
                if(grass){
                    if(parsed.Materials[at].TextureHashes.Count>1)material.SetTexture("_MaskTex",Texture(parsed.Materials[at].TextureHashes[1]));
                    int parameter=Section(0x6a)+b.U16(record+2)*16;
                    for(int n=1;n<=2;n++)material.SetVector("_Param"+n,new Vector4(b.F32(parameter+n*16),b.F32(parameter+n*16+4),b.F32(parameter+n*16+8),b.F32(parameter+n*16+12)));
                    int mask=Section(0x88)+(b.U16(record+14)+1)*32;
                    material.SetVector("_MaskRow0",new Vector4(b.F32(mask),b.F32(mask+4),b.F32(mask+8),0));
                    material.SetVector("_MaskRow1",new Vector4(b.F32(mask+16),b.F32(mask+20),b.F32(mask+24),0));
                }
                if(b.U8(record+12)>0){int uv=Section(0x88)+b.U16(record+14)*32;
                    material.SetVector("_UvRow0",new Vector4(b.F32(uv),b.F32(uv+4),b.F32(uv+8),0));
                    material.SetVector("_UvRow1",new Vector4(b.F32(uv+16),b.F32(uv+20),b.F32(uv+24),0));
                }
            }
            Loading.UpdateProgress((i+1)*100/models.Length);yield return null;
        }
    }
    public void Dispose(){if(Root!=null){Root.SetActive(false);CharacterManager.DestroyRuntimeObject(Root);Root=null;}}
}
