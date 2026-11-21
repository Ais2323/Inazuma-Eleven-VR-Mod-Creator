using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;
using UnityEngine;

internal sealed class HissatsuActor
{
    internal readonly GameObject Root;
    internal readonly G4Skeleton Rig;
    internal readonly Matrix4x4[] Pose;
    readonly Matrix4x4[] inverse,rest,local,matrices,skin;
    readonly bool[] done;
    readonly bool[] animated;
    readonly Vector3[] animatedPosition,animatedScale;
    readonly Quaternion[] animatedRotation;
    readonly bool cumulativeScale;
    readonly List<Skin> skins=new();
    readonly List<Material> materials=new();
    readonly Dictionary<Material,Vector2> uvOffsets=new();
    readonly Dictionary<Material,Color> colors=new();
    readonly Dictionary<uint,List<(Material material,int slot)>> uvTargets=new();
    readonly Dictionary<Material,Vector4[]> uvRows=new();
    readonly Dictionary<Material,Vector4[]> parameters=new();
    internal readonly List<G4AnimationBank> MaterialBanks=new();
    internal readonly List<G4AnimationBank> VisibilityBanks=new();
    internal G4AnimationBank EmbeddedMotion;
    internal HissatsuParticles Particles;
    readonly Dictionary<string,Texture2D> effectTextures=new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Warnings=new();
    sealed class Skin { internal Mesh Mesh; internal Renderer Renderer; internal Vector3[] Vertices,Normals,Output,OutputNormals; internal G4ModelParser.Part Part; internal int[] Palette; internal int Rigid; }
    static readonly Matrix4x4 Mirror=Matrix4x4.Scale(new Vector3(1,1,-1));
    static G4Skeleton ReadRig(byte[] bytes,string path)
    {
        try{return G4Skeleton.Read(bytes,path,"Event resource");}
        catch(InvalidDataException){
            // Older event/point rigs put the name directory at 0x30.
            var copy=(byte[])bytes.Clone();copy[0x32]=copy[0x30];copy[0x33]=copy[0x31];
            return G4Skeleton.Read(copy,path,"Event resource (legacy name directory)");
        }
    }
    internal HissatsuActor(string name,G4Skeleton rig,Transform parent,bool cumulativeScale=false)
    {
        Root=new GameObject(name);Root.transform.SetParent(parent,false);Rig=rig;this.cumulativeScale=cumulativeScale;
        int count=rig.Names.Length;Pose=new Matrix4x4[count];inverse=new Matrix4x4[count];rest=new Matrix4x4[count];local=new Matrix4x4[count];matrices=new Matrix4x4[count];skin=new Matrix4x4[count];done=new bool[count];
        animated=new bool[count];animatedPosition=new Vector3[count];animatedScale=new Vector3[count];animatedRotation=new Quaternion[count];
        for(int i=0;i<count;i++){rest[i]=Mirror*rig.Bind[i]*Mirror;inverse[i]=rest[i].inverse;Pose[i]=rest[i];}
        for(int i=0;i<count;i++){int p=rig.Parents[i];local[i]=p<count&&p!=i?rest[p].inverse*rest[i]:rest[i];}
    }
    internal void LoadModel(string relative,string textureRelative=null)
    {
        relative=relative.Replace('\\','/');if(relative.StartsWith("chr/",StringComparison.OrdinalIgnoreCase))relative=relative.Substring(4);
        string full=Path.Combine(DumpDataManager.CommonPath,"chr",relative);
        byte[] md=File.ReadAllBytes(full),mg=File.ReadAllBytes(Path.ChangeExtension(full,".g4mg"));
        LoadGeometry(full,md,mg,textureRelative==null?null:Path.Combine("chr",textureRelative.Replace('\\','/')),null);
    }
    internal void LoadGeometry(string full,byte[] metadata,byte[] geometry,string textureRelative,byte[] textures)
    {
        string relative=full.Substring(DumpDataManager.CommonPath.TrimEnd('\\','/').Length+1);
        var parsed=G4ModelParser.Parse(metadata,geometry,full);
        foreach(var part in parsed.Parts)
            if(part.BonePalette!=null&&part.BonePalette.Any(h=>!Rig.ByHash.ContainsKey(h)))
                throw new InvalidDataException(full+": mesh references bones absent from the animation rig");
        string folder=Path.GetDirectoryName(relative),name=Path.GetFileNameWithoutExtension(relative);
        var obj=G4ModelLoader.CreateModel(folder,name,textureName:textureRelative==null?name:Path.GetFileNameWithoutExtension(textureRelative),
            texturePath:textureRelative==null?folder:Path.GetDirectoryName(textureRelative),metadataOverride:metadata,geometryOverride:geometry,
            textureContainerOverride:textures,skeletonOverride:Rig,headLocalSkinning:false,
            meshCreated:(filter,index)=>{
                var part=parsed.Parts.Single(p=>p.SourceMeshIndex==index);var mesh=filter.sharedMesh;mesh.MarkDynamic();
                mesh.uv2=part.ExtraUV?[0]??part.UV;mesh.uv4=part.ExtraUV?[2]??part.UV;
                mesh.SetUVs(4,part.ExtraUV?[3]??part.UV);mesh.SetUVs(5,part.ExtraUV?[4]??part.UV);
                mesh.SetUVs(6,part.EffectControls??Enumerable.Repeat(Vector4.one,mesh.vertexCount).ToArray());
                mesh.uv2=part.ExtraUV?[0]??part.UV;mesh.uv4=part.ExtraUV?[2]??part.UV;
                var palette=part.BonePalette?.Select(h=>Rig.ByHash.TryGetValue(h,out int at)?at:-1).ToArray();
                if(palette!=null&&palette.Any(i=>i<0))throw new InvalidDataException(full+": mesh references bones absent from the animation rig");
                skins.Add(new Skin{Mesh=mesh,Renderer=filter.GetComponent<Renderer>(),Part=part,Vertices=mesh.vertices,Normals=mesh.normals,Output=new Vector3[mesh.vertexCount],OutputNormals=new Vector3[mesh.vertexCount],Palette=palette,Rigid=Rig.ByHash.TryGetValue(part.NodeHash,out int bone)?bone:-1});
            });
        obj.transform.SetParent(Root.transform,false);
        foreach(var renderer in obj.GetComponentsInChildren<Renderer>()){
            var material=renderer.sharedMaterial;if(material==null||uvOffsets.ContainsKey(material))continue;
            materials.Add(material);uvOffsets.Add(material,material.mainTextureOffset);colors.Add(material,material.color);
        }
        if(relative.Replace('\\','/').StartsWith("effect/",StringComparison.OrdinalIgnoreCase)){
            byte[] container=textures??File.ReadAllBytes(Path.Combine(DumpDataManager.Dx11Path,folder,name+".g4tx"));
            ConfigureEffects(metadata,parsed,obj,container);
        }
        else ConfigureCharacterUvs(metadata,parsed,obj,()=>textures??File.ReadAllBytes(Path.Combine(DumpDataManager.Dx11Path,
            textureRelative==null?Path.Combine(folder,name+".g4tx"):Path.ChangeExtension(textureRelative,".g4tx"))));
    }
    internal void LightTint(Color color){foreach(var material in materials)if(material.HasProperty("_LightTint"))material.SetColor("_LightTint",color);}
    void ConfigureCharacterUvs(byte[] bytes,G4ModelParser.Model model,GameObject obj,Func<byte[]> readTextures)
    {
        var b=new G4ModelParser.Binary(bytes,"Event character UV");int bias=b.U16(10)*4;int Section(int field)=>bias+b.U16(field)*4;
        List<G4TextureParser.TextureData> characterTextures=null;
        foreach(var material in obj.GetComponentsInChildren<Renderer>().Select(r=>r.sharedMaterial).Distinct()){
            material.shader=Resources.Load<Shader>("G4EventCharacter");
            int index=model.Materials.FindIndex(m=>m.Name==material.name);if(index<0)continue;
            // Some native character props split a glass shell into an opaque
            // surface record and an additive reflection record on the same
            // geometry. The shell has no diffuse vertex contribution. Drawing
            // it with the ordinary cutout pass conceals the interior completely.
            var surfaces=model.Parts.Where(p=>p.Material==index).ToArray();
            bool SameShell(G4ModelParser.Part a,G4ModelParser.Part c)=>a.Positions.Length==c.Positions.Length&&a.Indices.Length==c.Indices.Length&&a.Positions.OrderBy(v=>v.x).ThenBy(v=>v.y).ThenBy(v=>v.z).SequenceEqual(c.Positions.OrderBy(v=>v.x).ThenBy(v=>v.y).ThenBy(v=>v.z));
            bool reflectionShell=surfaces.Length>0&&surfaces.All(p=>p.Colors.All(c=>c.r==0))&&
                surfaces.All(p=>model.Parts.Any(other=>other.Material!=index&&SameShell(other,p)&&
                    model.Materials[other.Material].TextureHashes.SequenceEqual(model.Materials[index].TextureHashes)&&NativeAdditive(other.Material)));
            bool NativeAdditive(int at){int r=Section(0x64)+at*16;var states=new Dictionary<byte,byte>();for(int s=0;s<b.U8(r+11);s++){int a=Section(0x6c)+(b.U16(r+4)+s)*2;states[b.U8(a)]=b.U8(a+1);}return states.TryGetValue(6,out byte blend)&&blend==1&&states.TryGetValue(10,out byte destination)&&destination==1;}
            // The second shell is the reflection layer itself; apply the same
            // treatment when its paired diffuse-free surface was identified.
            bool reflectionLayer=NativeAdditive(index)&&surfaces.Length>0&&surfaces.All(p=>p.Colors.All(c=>c.r==0))&&
                surfaces.All(p=>model.Parts.Any(other=>other.Material!=index&&SameShell(other,p)&&!NativeAdditive(other.Material)&&
                    model.Materials[other.Material].TextureHashes.SequenceEqual(model.Materials[index].TextureHashes)));
            if(reflectionShell||reflectionLayer){
                material.SetFloat("_Glass",1);material.SetFloat("_SrcBlend",(int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetFloat("_DstBlend",reflectionLayer?(int)UnityEngine.Rendering.BlendMode.One:(int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
                material.SetFloat("_ZWrite",0);material.SetFloat("_Cutoff",0);material.renderQueue=reflectionLayer?3001:3000;
                characterTextures??=G4TextureParser.Parse(readTextures(),"Event glass textures",Warnings,baseOnly:false);
                var reflection=G4TextureParser.ChooseSuffix(model.Materials[index],characterTextures,"sp");
                if(reflection!=null){
                    var resources=obj.GetComponent<RuntimeHeadResources>();
                    var texture=resources.Own(new Texture2D(reflection.Width,reflection.Height,reflection.Format,reflection.MipCount,true){name=reflection.Name,wrapMode=TextureWrapMode.Clamp});
                    texture.LoadRawTextureData(reflection.Pixels);texture.Apply(false,true);material.SetTexture("_GlassReflection",texture);
                }
            }
            int record=Section(0x64)+index*16;if(b.U8(record+12)==0)continue;int start=b.U16(record+14),at=Section(0x88)+start*32;
            float a=b.F32(at),bb=b.F32(at+4),c=b.F32(at+8),d=b.F32(at+16),e=b.F32(at+20),f=b.F32(at+24);
            // G4ModelParser and DDS upload both preserve native row order.
            // These matrices must not receive Blender's flipped-V conversion.
            var rows=new[]{new Vector4(a,bb,c,0),new Vector4(d,e,f,0)};uvRows[material]=rows;
            material.SetVector("_EventUvRow0",rows[0]);material.SetVector("_EventUvRow1",rows[1]);
            uint hash=b.U32(Section(0x7a)+start*4);if(!uvTargets.TryGetValue(hash,out var targets))uvTargets[hash]=targets=new List<(Material,int)>();targets.Add((material,0));
        }
    }
    void ConfigureEffects(byte[] bytes,G4ModelParser.Model model,GameObject obj,byte[] container)
    {
        var b=new G4ModelParser.Binary(bytes,"Event material");int bias=b.U16(10)*4;int Section(int field)=>bias+b.U16(field)*4;
        var shader=Resources.Load<Shader>("G4EventEffect");if(shader==null)throw new InvalidDataException("Missing G4EventEffect shader");
        var textureData=G4TextureParser.Parse(container,"Event textures",new List<string>(),baseOnly:false);
        var resources=obj.GetComponent<RuntimeHeadResources>();var loaded=new Dictionary<uint,Texture2D>();
        Texture2D Texture(uint hash){
            if(loaded.TryGetValue(hash,out var cached))return cached;
            var data=textureData.SingleOrDefault(t=>t.Hash==hash);
            if(data==null){
                string shared=Path.Combine(DumpDataManager.Dx11Path,"effect","system");
                foreach(string file in System.IO.Directory.Exists(shared)?System.IO.Directory.GetFiles(shared,"*.g4tx",SearchOption.AllDirectories):Array.Empty<string>()){
                    var candidates=G4TextureParser.Parse(File.ReadAllBytes(file),file,new List<string>(),baseOnly:false);data=candidates.FirstOrDefault(t=>t.Hash==hash);if(data!=null)break;
                }
            }
            if(data==null){string warning="Missing native effect texture "+hash.ToString("X8")+"; displaying this sampler as white.";if(!Warnings.Contains(warning))Warnings.Add(warning);return Texture2D.whiteTexture;}
            // Native effect samplers are UNORM data, including gradient and mask maps.
            var texture=resources.Own(new Texture2D(data.Width,data.Height,data.Format,data.MipCount,true){name=data.Name,wrapMode=TextureWrapMode.Repeat});
            texture.LoadRawTextureData(data.Pixels);texture.Apply(false,true);loaded.Add(hash,texture);effectTextures[data.Name]=texture;return texture;
        }
        foreach(var material in obj.GetComponentsInChildren<Renderer>().Select(r=>r.sharedMaterial).Distinct()){
            int index=model.Materials.FindIndex(m=>m.Name==material.name);if(index<0)continue;
            int record=Section(0x64)+index*16,color=Section(0x66)+b.U16(record)*48;
            material.shader=shader;material.color=new Color(b.F32(color),b.F32(color+4),b.F32(color+8),b.F32(color+12));
            colors[material]=material.color;material.SetFloat("_Gain",1+b.F32(color+16));
            var states=new Dictionary<byte,byte>();for(int i=0;i<b.U8(record+11);i++){int at=Section(0x6c)+(b.U16(record+4)+i)*2;states[b.U8(at)]=b.U8(at+1);}
            bool blend=states.TryGetValue(6,out byte enabled)&&enabled!=0;
            material.SetFloat("_SrcBlend",blend?NativeBlend(states.TryGetValue(9,out byte src)?src:(byte)4):1);
            material.SetFloat("_DstBlend",blend?NativeBlend(states.TryGetValue(10,out byte dest)?dest:(byte)5):0);
            material.SetFloat("_ZWrite",states.TryGetValue(5,out byte write)?write:0);
            material.SetFloat("_ZTest",states.TryGetValue(4,out byte test)&&test==0?(int)UnityEngine.Rendering.CompareFunction.Always:(int)UnityEngine.Rendering.CompareFunction.LessEqual);
            material.renderQueue=blend?3000:2000;
            uint shaderHash=b.U32(Section(0x78)+b.U8(record+9)*4);
            if(shaderHash==0xd05b3d9d)material.shader=Resources.Load<Shader>("G4EventDistortion")??throw new InvalidDataException("Missing G4EventDistortion shader");
            bool solidToon=shaderHash==0x81550fea;
            material.SetFloat("_SolidToon",solidToon?1:0);
            material.SetFloat("_Family",solidToon?1:shaderHash==0x2c30c634?2:shaderHash==0x5f8ef7cf?3:shaderHash==0xd05b3d9d?4:shaderHash==G4TextureParser.Crc32("Effect_T3Threshold")||shaderHash==G4TextureParser.Crc32("Effect_T3ThresholdF")?5:0);
            material.SetFloat("_Flow",shaderHash==G4TextureParser.Crc32("Effect_T3ThresholdF")?1:0);
            material.SetFloat("_Threshold",shaderHash==0x60f87c7d||shaderHash==0xc10dec69?1:0);
            material.SetFloat("_Pattern",shaderHash==0x93094f5e?1:0);
            var values=new Vector4[Math.Min(8,(int)b.U8(record+10))];
            for(int i=0;i<values.Length;i++){
                int at=Section(0x6a)+(b.U16(record+2)+i)*16;
                values[i]=new Vector4(b.F32(at),b.F32(at+4),b.F32(at+8),b.F32(at+12));material.SetVector("_Param"+i,values[i]);
            }
            parameters[material]=values;
            var rows=new Vector4[12];for(int t=0;t<6;t++){rows[t*2]=new Vector4(1,0,0,0);rows[t*2+1]=new Vector4(0,1,0,0);}
            int textures=b.U8(record+12),start=b.U16(record+14);
            var address=new Vector4(1,1,1,1);
            for(int t=0;t<Math.Min(6,textures);t++){
                int reference=Section(0x68)+(b.U16(record+6)+t)*6;
                // Distortion's third reference is the native scene color target,
                // supplied by G4EventDistortion's GrabPass rather than a DDS.
                if(shaderHash==0xd05b3d9d&&t>=2)continue;
                var texture=Texture(b.U32(Section(0x76)+b.U8(reference)*4));
                int sampler=Section(0x6e)+b.U16(reference+4)*2;
                for(int i=0;i<3;i++){
                    int key=b.U8(sampler+i*2),value=b.U8(sampler+i*2+1);
                    if((key==1||key==2)&&value>=1&&value<=3){if(t<2)address[t*2+key-1]=value;else material.SetFloat(UvPrefix(material,t)+(key==1?"AddressU":"AddressV"),value);}
                }
                if(t==0)material.mainTexture=texture;
                else if(t==1)material.SetTexture("_MaskTex",texture);
                else material.SetTexture(t==2?"_ExtraTex":"_Tex"+t,texture);
            }
            material.SetVector("_AddressModes",address);
            // Only T1M1 families use sampler one as an opacity mask.
            material.SetFloat("_UseMask",shaderHash==0xd1702270||shaderHash==0x42724112?1:0);
            for(int t=0;t<Math.Min(6,textures);t++){
                int uv=Section(0x88)+(start+t)*32;
                float a=b.F32(uv),bb=b.F32(uv+4),c=b.F32(uv+8),d=b.F32(uv+16),e=b.F32(uv+20),f=b.F32(uv+24);
                rows[t*2]=new Vector4(a,bb,c,0);rows[t*2+1]=new Vector4(d,e,f,0);
                uint hash=b.U32(Section(0x7a)+(start+t)*4);
                if(!uvTargets.TryGetValue(hash,out var targets))uvTargets[hash]=targets=new List<(Material,int)>();targets.Add((material,t));
            }
            uvRows[material]=rows;
            for(int t=0;t<6;t++){string prefix=UvPrefix(material,t);material.SetVector(prefix+"Row0",rows[t*2]);material.SetVector(prefix+"Row1",rows[t*2+1]);}
        }
        foreach(var data in textureData)if(!effectTextures.ContainsKey(data.Name))Texture(data.Hash);
    }
    Material ParticleMaterial(string textureName)
    {
        if(!effectTextures.TryGetValue(textureName,out var texture))throw new InvalidDataException("Missing particle texture "+textureName);
        var resources=Root.GetComponent<RuntimeHeadResources>()??Root.AddComponent<RuntimeHeadResources>();
        var material=resources.Own(new Material(Resources.Load<Shader>("G4EventEffect")){name=textureName+" particle",mainTexture=texture});
        material.SetVector("_AddressModes",new Vector4(3,3,3,3));
        material.SetFloat("_DstBlend",(int)UnityEngine.Rendering.BlendMode.One);return material;
    }
    internal void Evaluate(G4AnimationBank.Clip clip,float seconds)
    {
        Array.Copy(local,matrices,local.Length);
        Array.Clear(animated,0,animated.Length);
        float frame=clip==null?0:clip.Start+seconds*clip.Fps;
        if(clip!=null)foreach(var track in clip.Tracks){
            if(!Rig.ByHash.TryGetValue(track.Target,out int bone))continue;
            var m=local[bone];Vector3 p=m.GetColumn(3),s=m.lossyScale;Quaternion q=m.rotation;
            foreach(var curve in track.Curves){var v=curve.Sample(frame);int t=curve.Type;
                if(t>=1&&t<=3)s[t-1]=clip.Additive?s[t-1]*(1+v.x):v.x;
                else if(t>=10&&t<=12){float value=t==12?-v.x:v.x;p[t-10]=clip.Additive?p[t-10]+value:value;}
                else if(t==9){var value=new Quaternion(-v.x,-v.y,v.z,v.w);q=clip.Additive?q*value:value;}
            }
            animated[bone]=true;animatedPosition[bone]=p;animatedRotation[bone]=q;animatedScale[bone]=s;
        }
        // Character tracks carry cumulative scale; effect rigs retain local
        // scale so a scale helper can enlarge its animated child mesh.
        for(int i=0;i<local.Length;i++)if(animated[i]){
            var scale=animatedScale[i];int parent=Rig.Parents[i];
            if(cumulativeScale&&parent<local.Length&&parent!=i&&animated[parent])
                for(int axis=0;axis<3;axis++)if(Mathf.Abs(animatedScale[parent][axis])>1e-9f)scale[axis]/=animatedScale[parent][axis];
            matrices[i]=Matrix4x4.TRS(animatedPosition[i],animatedRotation[i],scale);
        }
        Array.Clear(done,0,done.Length);
        void Build(int i){if(done[i])return;int parent=Rig.Parents[i];if(parent<local.Length&&parent!=i){Build(parent);Pose[i]=Pose[parent]*matrices[i];}else Pose[i]=matrices[i];done[i]=true;}
        for(int i=0;i<local.Length;i++)Build(i);
        for(int i=0;i<Pose.Length;i++)skin[i]=Pose[i]*inverse[i];
        foreach(var item in skins){
            for(int v=0;v<item.Vertices.Length;v++){
                Vector3 p=Vector3.zero,n=Vector3.zero;float sum=0;
                if(item.Palette!=null)for(int w=0;w<8;w++){
                    float weight=item.Part.BoneWeights[v,w]/65535f;if(weight==0)continue;int bone=item.Palette[item.Part.BoneIndices[v,w]];
                    p+=skin[bone].MultiplyPoint3x4(item.Vertices[v])*weight;n+=skin[bone].MultiplyVector(item.Normals[v])*weight;sum+=weight;
                }
                if(sum==0){var transform=item.Rigid>=0?skin[item.Rigid]:Matrix4x4.identity;p=transform.MultiplyPoint3x4(item.Vertices[v]);n=transform.MultiplyVector(item.Normals[v]);}
                item.Output[v]=p;item.OutputNormals[v]=n.normalized;
            }
            item.Mesh.vertices=item.Output;item.Mesh.normals=item.OutputNormals;item.Mesh.RecalculateBounds();
        }
    }
    internal Matrix4x4 Bone(string name)=>Rig.ByHash.TryGetValue(G4TextureParser.Crc32(name),out int i)?Pose[i]:Matrix4x4.identity;
    internal void VisibilityFrame(string cut,float seconds)
    {
        foreach(var item in skins)item.Renderer.enabled=true;
        foreach(var bank in VisibilityBanks){var clip=bank.Find(cut);if(clip==null)continue;float frame=clip.Start+seconds*clip.Fps;
            foreach(var track in clip.Tracks)foreach(var curve in track.Curves){if(curve.Type!=15)continue;bool visible=curve.Sample(frame).x!=0;
                foreach(var item in skins)if(item.Part.NodeHash==track.Target||G4TextureParser.Crc32(item.Part.Name)==track.Target)item.Renderer.enabled=visible;
            }
        }
    }
    static int NativeBlend(byte value)=>value switch{0=>0,1=>1,2=>3,3=>6,4=>5,5=>10,6=>7,7=>8,8=>2,9=>4,10=>9,_=>throw new InvalidDataException("Unsupported native effect blend factor "+value)};
    static string UvPrefix(Material material,int slot)=>material.HasProperty("_EventUvRow0")?"_EventUv":slot==0?"_Uv":slot==1?"_Mask":slot==2?"_Extra":"_Tex"+slot;
    internal void MaterialFrame(string cut,float seconds)
    {
        foreach(var material in materials){material.mainTextureOffset=uvOffsets[material];material.color=colors[material];}
        foreach(var entry in uvRows)for(int slot=0;slot<entry.Value.Length/2;slot++){string prefix=UvPrefix(entry.Key,slot);entry.Key.SetVector(prefix+"Row0",entry.Value[slot*2]);entry.Key.SetVector(prefix+"Row1",entry.Value[slot*2+1]);}
        foreach(var entry in parameters)for(int i=0;i<entry.Value.Length;i++)entry.Key.SetVector("_Param"+i,entry.Value[i]);
        foreach(var bank in MaterialBanks){var clip=bank.Find(cut);if(clip==null&&bank.Clips.Any(c=>System.Text.RegularExpressions.Regex.IsMatch(c.Name,@"^c\d+$")))continue;clip??=bank.Clips.FirstOrDefault();if(clip==null)continue;
            float frame=clip.Start+seconds*clip.Fps;
            foreach(var track in clip.Tracks){
                if(uvTargets.TryGetValue(track.Target,out var targets))foreach(var target in targets)foreach(var curve in track.Curves){
                    if(curve.Type!=10&&curve.Type!=11)continue;
                    float value=curve.Sample(frame).x;string prefix=UvPrefix(target.material,target.slot),u=prefix+"Row0",v=prefix+"Row1";
                    var rowU=target.material.GetVector(u);var rowV=target.material.GetVector(v);
                    // G4TP keys are offsets before the authored UV rotation/scale.
                    // Recover the other offset so independently keyed axes compose.
                    float determinant=rowU.x*rowV.y-rowU.y*rowV.x;if(Mathf.Abs(determinant)<1e-10f)continue;
                    float c=rowU.z,f=rowV.z;
                    float tx=(-rowV.y*c+rowU.y*f)/determinant,ty=(rowV.x*c-rowU.x*f)/determinant;
                    if(curve.Type==10)tx=value;else ty=value;
                    rowU.z=-rowU.x*tx-rowU.y*ty;rowV.z=-rowV.x*tx-rowV.y*ty;
                    target.material.SetVector(u,rowU);target.material.SetVector(v,rowV);
                }
                foreach(var material in materials.Where(m=>G4TextureParser.Crc32(m.name)==track.Target)){
                foreach(var c in track.Curves){var v=c.Sample(frame);var color=material.color;
                    if(c.Type>=16&&c.Type<=19){color[c.Type-16]=v.x;material.color=color;}
                    else if(c.Type>=32&&c.Type<=35&&c.Parameter<8){string name="_Param"+c.Parameter;if(material.HasProperty(name)){var parameter=material.GetVector(name);parameter[c.Type-32]=v.x;material.SetVector(name,parameter);}}
                }
                }
            }
        }
    }
    internal static HissatsuActor Demo(string name,Transform parent)
    {
        var body=new UniformCatalog().Body;var rig=NativeBodyShape.Skeleton(body);
        var actor=new HissatsuActor(name,rig,parent,cumulativeScale:true);
        try{
            string directory=Path.Combine(DumpDataManager.CommonPath,"gamedata","character");
            var bases=CfgTable.Read(File.ReadAllBytes(CharacterIdentityCatalog.Find(directory,"chara_base_")),"Demo character");
            var models=CfgTable.Read(File.ReadAllBytes(CharacterIdentityCatalog.Find(directory,"chara_model_")),"Demo model");
            var character=bases.Rows.Single(r=>r.Name=="CHARA_BASE_INFO"&&r.Text(1)=="c01000010");
            var model=models.Rows.Single(r=>r.Name=="CHARA_MODEL_INFO"&&r.UInt(0)==character.UInt(6));
            uint rgba=model.UInt(16);
            var skinColor=new Color((rgba>>24)/255f,((rgba>>16)&255)/255f,((rgba>>8)&255)/255f,(rgba&255)/255f);
            var kit=NativeUniformFashion.Resolve(CharacterInfoController.DefaultUniformStem+"_10",body)??throw new InvalidDataException("Missing demo uniform");
            actor.LoadModel(kit.Uniform,kit.UniformTexture);
            foreach(var material in actor.materials)if(material.HasProperty("_SkinColor"))material.SetColor("_SkinColor",skinColor);
            int skinStart=actor.materials.Count;actor.LoadModel(kit.Skin,kit.SkinTexture);
            foreach(var material in actor.materials.Skip(skinStart)){
                if(material.HasProperty("_SkinColor"))material.SetColor("_SkinColor",skinColor);
                if(material.HasProperty("_WholeSkin"))material.SetFloat("_WholeSkin",1);
            }
            var parts=CfgTable.Read(File.ReadAllBytes(CharacterIdentityCatalog.Find(directory,"chara_parts_")),"Demo shoes");
            int shoeIndex=parts.Rows.FindIndex(r=>r.Name=="CHARA_PARTS_SHOES_MODEL"&&r.UInt(0)==model.UInt(6));
            if(shoeIndex<0||shoeIndex+1>=parts.Rows.Count||parts.Rows[shoeIndex+1].Name!="CHARA_PARTS_SHOES_MODEL_REF_INFO")
                throw new InvalidDataException("Missing demo shoe model reference");
            var reference=parts.Rows[shoeIndex+1];int family=(int)body.Int(5)%4;
            var shoes=parts.Rows.Where(r=>r.Name=="CHARA_PARTS_SHOES_INFO")
                .Skip(checked((int)reference.Int(0))).Take(checked((int)reference.Int(1)))
                .SingleOrDefault(r=>r.Int(2)==family)??throw new InvalidDataException("Missing demo shoes for skeleton family "+family);
            actor.LoadModel(shoes.Text(0),shoes.Text(1));
            string face=CharacterMakerBatchExporter.FindFaceModel("c01000010");actor.LoadModel(Path.Combine("_face",face));
            return actor;
        }catch{CharacterManager.DestroyRuntimeObject(actor.Root);throw;}
    }
    internal static HissatsuActor Resource(string name,string path,Transform parent,bool cumulativeScale=false)
    {
        path=HissatsuEvent.Resolve(path);
        if(path.EndsWith(".objbin",StringComparison.OrdinalIgnoreCase)){
            string packed=Path.ChangeExtension(path,".g4pkm");if(File.Exists(packed))path=packed;
        }
        byte[] metadata=null,geometry=null;Dictionary<string,byte[]> blocks;
        if(path.EndsWith(".g4sk",StringComparison.OrdinalIgnoreCase)){blocks=new Dictionary<string,byte[]>{{"G4SK",File.ReadAllBytes(path)}};}
        else{
            if(!File.Exists(path)&&File.Exists(Path.ChangeExtension(path,".g4pkm")))path=Path.ChangeExtension(path,".g4pkm");
            blocks=G4AnimationBank.Package(File.ReadAllBytes(path),path);
            blocks.TryGetValue("G4MD",out metadata);
            if(metadata!=null)geometry=blocks.TryGetValue("G4MG",out var packed)?packed:File.ReadAllBytes(Path.ChangeExtension(path,".g4mg"));
        }
        G4Skeleton rig;
        if(blocks.TryGetValue("G4SK",out var sk))rig=ReadRig(sk,path);
        else {string stem=Path.GetFileNameWithoutExtension(path);string candidate=Path.Combine(DumpDataManager.CommonPath,"chr",stem,stem+".g4sk");rig=ReadRig(File.ReadAllBytes(candidate),candidate);}
        var actor=new HissatsuActor(name,rig,parent,cumulativeScale:cumulativeScale);
        try{
            if(metadata!=null)actor.LoadGeometry(path,metadata,geometry,null,null);
            if(blocks.TryGetValue("G4MT",out var motion))actor.EmbeddedMotion=G4AnimationBank.Read(motion,path+"/G4MT");
            foreach(string kind in new[]{"G4MA","G4TP"})if(blocks.TryGetValue(kind,out var material))actor.MaterialBanks.Add(G4AnimationBank.Read(material,path+"/"+kind));
            if(blocks.TryGetValue("G4VS",out var visibility))actor.VisibilityBanks.Add(G4AnimationBank.Read(visibility,path+"/G4VS"));
            string particles=Path.ChangeExtension(path,".ptlb");
            if(File.Exists(particles))try{actor.Particles=new HissatsuParticles(particles,actor.Root.transform,actor.ParticleMaterial);}catch(Exception e){actor.Warnings.Add(particles+": "+e.Message);}
            return actor;
        }catch{CharacterManager.DestroyRuntimeObject(actor.Root);throw;}
    }
}
