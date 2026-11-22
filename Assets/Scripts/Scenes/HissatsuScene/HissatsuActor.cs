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
    readonly Vector3[] animatedPosition,animatedScale,restScale;
    readonly Quaternion[] animatedRotation;
    readonly bool cumulativeScale;
    readonly byte[] billboardBones;
    readonly List<Skin> skins=new();
    readonly List<Material> materials=new();
    readonly Dictionary<Material,Vector2> uvOffsets=new();
    readonly Dictionary<Material,Color> colors=new();
    readonly Dictionary<Material,Vector4> ambientColors=new();
    readonly Dictionary<uint,List<(Material material,int slot)>> uvTargets=new();
    readonly Dictionary<Material,Vector4[]> uvRows=new();
    readonly Dictionary<Material,Vector4[]> parameters=new();
    internal readonly List<G4AnimationBank> MaterialBanks=new();
    internal readonly List<G4AnimationBank> VisibilityBanks=new();
    internal G4AnimationBank EmbeddedMotion;
    internal HissatsuParticles Particles;
    readonly Dictionary<string,Texture2D> effectTextures=new(StringComparer.OrdinalIgnoreCase);
    internal readonly List<string> Warnings=new();
    sealed class Skin { internal Mesh Mesh; internal Renderer Renderer; internal Vector3[] Vertices,Normals,Output,OutputNormals; internal G4ModelParser.Part Part; internal int[] Palette; internal Vector4[] OutputTangents; internal int Rigid,Billboard; internal bool Drawable=true; }
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
        int count=rig.Names.Length;billboardBones=new byte[count];
        if(rig.Bytes!=null&&rig.Bytes.Length>=0x30&&(rig.Bytes[0x22]&2)!=0){
            int flags=0x40+BitConverter.ToUInt16(rig.Bytes,0x2e)*4;
            if(flags+count>rig.Bytes.Length)throw new InvalidDataException(rig.Path+": invalid billboard bone table");
            for(int i=0;i<count;i++)billboardBones[i]=(byte)((rig.Bytes[flags+i]>>1)&15);
        }
        Pose=new Matrix4x4[count];inverse=new Matrix4x4[count];rest=new Matrix4x4[count];local=new Matrix4x4[count];matrices=new Matrix4x4[count];skin=new Matrix4x4[count];done=new bool[count];
        restScale=new Vector3[count];animated=new bool[count];animatedPosition=new Vector3[count];animatedScale=new Vector3[count];animatedRotation=new Quaternion[count];
        for(int i=0;i<count;i++){rest[i]=Mirror*rig.Bind[i]*Mirror;inverse[i]=rest[i].inverse;Pose[i]=rest[i];}
        // Animation rest locals are a separate G4SK table. The mesh binding
        // matrices can describe a different pose, including a different root
        // orientation/translation; deriving locals from them moves balls and
        // applies a second scene placement to skinned effect meshes.
        var data=new G4ModelParser.Binary(rig.Bytes,rig.Path);
        int locals=0x40+data.U16(0x26)*4;data.Range(locals,count*48L);
        for(int i=0;i<count;i++){
            int at=locals+i*48;
            var scale=new Vector3(data.F32(at),data.F32(at+4),data.F32(at+8));
            var rotation=new Quaternion(data.F32(at+16),data.F32(at+20),data.F32(at+24),data.F32(at+28)).normalized;
            var position=new Vector3(data.F32(at+32),data.F32(at+36),data.F32(at+40));
            restScale[i]=scale;local[i]=Mirror*Matrix4x4.TRS(position,rotation,scale)*Mirror;
        }
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
        var meshMetadata=new G4ModelParser.Binary(metadata,full);
        int materialTable=meshMetadata.U16(10)*4+meshMetadata.U16(0x64)*4;
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
                skins.Add(new Skin{Mesh=mesh,Renderer=filter.GetComponent<Renderer>(),Part=part,Vertices=mesh.vertices,Normals=mesh.normals,Output=new Vector3[mesh.vertexCount],OutputNormals=new Vector3[mesh.vertexCount],Palette=palette,OutputTangents=part.Tangents==null?null:new Vector4[mesh.vertexCount],Billboard=meshMetadata.U8(materialTable+part.Material*16+13),Rigid=Rig.ByHash.TryGetValue(part.NodeHash,out int bone)?bone:-1});
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
            int colorRecord=Section(0x66)+b.U16(Section(0x64)+index*16)*48;
            ambientColors[material]=new Vector4(b.F32(colorRecord+16),b.F32(colorRecord+20),b.F32(colorRecord+24),b.F32(colorRecord+28));
            material.SetFloat("_Gain",1+ambientColors[material].x);
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
    internal void IncomingBallAppearance()
    {
        foreach(var material in materials)
            if(material.HasProperty("_BallAuraColumn"))material.SetFloat("_BallAuraColumn",4);
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
                string common=Path.Combine(DumpDataManager.Dx11Path,"effect","battle","common","effCmnTex","effCmnTex.g4tx");
                if(File.Exists(common))data=G4TextureParser.Parse(File.ReadAllBytes(common),common,new List<string>(),baseOnly:false).FirstOrDefault(t=>t.Hash==hash);
            }
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
        // Native rendering uses staged layer ranges, not a numeric descending
        // priority: 25-29, then 37-38, then 30-36, then 40-49. Preserve mesh
        // directory order inside a layer, including appended background meshes.
        var drawOrder=model.Parts.OrderBy(part=>NativeDrawKey(b.U8(Section(0x64)+part.Material*16+8)))
            .ThenBy(part=>part.SourceMeshIndex).Select((part,rank)=>(part.SourceMeshIndex,rank))
            .ToDictionary(item=>item.SourceMeshIndex,item=>item.rank);
        foreach(var material in obj.GetComponentsInChildren<Renderer>().Select(r=>r.sharedMaterial).Distinct()){
            int index=model.Materials.FindIndex(m=>m.Name==material.name);if(index<0)continue;
            int record=Section(0x64)+index*16,color=Section(0x66)+b.U16(record)*48;
            material.shader=shader;material.color=new Color(b.F32(color),b.F32(color+4),b.F32(color+8),b.F32(color+12));
            colors[material]=material.color;
            if(NativeDrawKey(b.U8(record+8))<0){
                foreach(var item in skins.Where(item=>item.Renderer.sharedMaterial==material)){item.Drawable=false;item.Renderer.enabled=false;}
                continue;
            }
            ambientColors[material]=new Vector4(b.F32(color+16),b.F32(color+20),b.F32(color+24),b.F32(color+28));
            material.SetFloat("_Gain",1+b.F32(color+16));
            var states=new Dictionary<byte,byte>();for(int i=0;i<b.U8(record+11);i++){int at=Section(0x6c)+(b.U16(record+4)+i)*2;states[b.U8(at)]=b.U8(at+1);}
            bool blend=states.TryGetValue(6,out byte enabled)&&enabled!=0;
            material.SetFloat("_SrcBlend",blend?NativeBlend(states.TryGetValue(9,out byte src)?src:(byte)4):1);
            material.SetFloat("_DstBlend",blend?NativeBlend(states.TryGetValue(10,out byte dest)?dest:(byte)5):0);
            material.SetFloat("_ZWrite",states.TryGetValue(5,out byte write)?write:0);
            material.SetFloat("_Cull",states.TryGetValue(14,out byte cull)&&cull==1?2:0);
            material.SetFloat("_BlendOp",states.TryGetValue(7,out byte operation)&&operation==5?(int)UnityEngine.Rendering.BlendOp.ReverseSubtract:(int)UnityEngine.Rendering.BlendOp.Add);
            material.SetFloat("_ZTest",states.TryGetValue(4,out byte test)&&test==0?(int)UnityEngine.Rendering.CompareFunction.Always:(int)UnityEngine.Rendering.CompareFunction.LessEqual);
            // A translucent material may intentionally write depth. Keep its
            // transparent ordering: moving it ahead of the background makes the
            // invisible part of feather/sprite quads punch rectangular holes.
            material.renderQueue=blend?3000:2000;
            // Respect material layers, retaining source mesh order within each
            // layer instead of sorting large translucent spheres by bounds.
            foreach(var item in skins.Where(item=>item.Renderer.sharedMaterial==material))
                item.Renderer.sortingOrder=blend?drawOrder[item.Part.SourceMeshIndex]:0;
            uint shaderHash=b.U32(Section(0x78)+b.U8(record+9)*4);
            if(shaderHash==0){
                // An unbound placeholder has no native draw program. Rendering
                // it with the generic preview shader exposes its vertex colors
                // as an opaque surface (e.g. the red sphere in whs02340).
                foreach(var item in skins.Where(item=>item.Renderer.sharedMaterial==material)){item.Drawable=false;item.Renderer.enabled=false;}
                continue;
            }
            bool distortion=shaderHash==0xd05b3d9d||shaderHash==0xd1b3c917;
            if(distortion)material.shader=Resources.Load<Shader>("G4EventDistortion")??throw new InvalidDataException("Missing G4EventDistortion shader");
            bool solidToon=shaderHash==0x81550fea;
            material.SetFloat("_SolidToon",solidToon?1:0);
            material.SetFloat("_VDisp",shaderHash==0xee97bf86?1:0);
            material.SetFloat("_Family",shaderHash==G4TextureParser.Crc32("Effect_BallAura1")?9:shaderHash==G4TextureParser.Crc32("Effect_DifferenceFlowM")?8:shaderHash==G4TextureParser.Crc32("Effect_DifferenceFlow")?7:solidToon?1:shaderHash==0x2c30c634?2:shaderHash==0x5f8ef7cf||shaderHash==0xee97bf86?3:distortion?4:shaderHash==G4TextureParser.Crc32("Effect_Smoke2")?6:shaderHash==G4TextureParser.Crc32("Effect_T3Threshold")||shaderHash==G4TextureParser.Crc32("Effect_T3ThresholdF")?5:0);
            material.SetFloat("_DistortionBlur",shaderHash==0xd05b3d9d?1:0);
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
                if(distortion&&t>=2)continue;
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
                b.Range(uv,32);
                var coefficients=new[]{0,4,8,16,20,24}.Select(offset=>BitConverter.ToSingle(bytes,uv+offset)).ToArray();
                if(coefficients.All(value=>!float.IsNaN(value)&&!float.IsInfinity(value))){
                    rows[t*2]=new Vector4(coefficients[0],coefficients[1],coefficients[2],0);
                    rows[t*2+1]=new Vector4(coefficients[3],coefficients[4],coefficients[5],0);
                }else Warnings.Add(material.name+" sampler "+t+": non-finite rest UV; using neutral rest UV with native animation overrides.");
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
        // Native NonUniformScale cancels the parent's scale in the child's
        // basis, while retaining the full parent transform for its position.
        for(int i=0;i<local.Length;i++)if(animated[i])
            matrices[i]=Matrix4x4.TRS(animatedPosition[i],animatedRotation[i],animatedScale[i]);
        Array.Clear(done,0,done.Length);
        void Build(int i){if(done[i])return;int parent=Rig.Parents[i];if(parent<local.Length&&parent!=i){Build(parent);Pose[i]=Compose(parent,i);}else Pose[i]=matrices[i];done[i]=true;}
        for(int i=0;i<local.Length;i++)Build(i);
        UpdateMeshes(null);
    }
    Matrix4x4 Compose(int parent,int child)
    {
        var full=Pose[parent];if(!cumulativeScale)return full*matrices[child];
        var scale=animated[parent]?animatedScale[parent]:restScale[parent];
        var basis=full;
        for(int axis=0;axis<3;axis++)for(int row=0;row<3;row++)
            basis[row,axis]=Mathf.Abs(scale[axis])>1e-9f?full[row,axis]/scale[axis]:0;
        var result=basis*matrices[child];var position=full.MultiplyPoint3x4(matrices[child].GetColumn(3));
        result.SetColumn(3,new Vector4(position.x,position.y,position.z,1));return result;
    }
    readonly Dictionary<Material,(float src,float dst,float depth,int queue)> fadeStates=new();
    internal void Opacity(float opacity)
    {
        foreach(var material in materials){
            if(!fadeStates.TryGetValue(material,out var state)){
                state=(material.GetFloat("_SrcBlend"),material.GetFloat("_DstBlend"),material.GetFloat("_ZWrite"),material.renderQueue);fadeStates.Add(material,state);
            }
            if(material.HasProperty("_ModelOpacity"))material.SetFloat("_ModelOpacity",opacity);
            else {var color=material.color;color.a*=opacity;material.color=color;}
            bool fading=opacity<.999f;bool opaque=state.src==1&&state.dst==0;
            material.SetFloat("_SrcBlend",fading&&opaque?(int)UnityEngine.Rendering.BlendMode.SrcAlpha:state.src);
            material.SetFloat("_DstBlend",fading&&opaque?(int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha:state.dst);
            material.SetFloat("_ZWrite",fading?0:state.depth);material.renderQueue=fading?3000:state.queue;
        }
    }
    // Native material +0x0D and G4SK bone flags opt into camera-facing
    // placement. Rebuild from this frame's animation every time (no accumulation).
    internal void ApplyBillboards(Camera camera)
    {
        if(!billboardBones.Any(mode=>mode!=0)&&!skins.Any(item=>item.Billboard!=0))return;
        var cameraLocal=Root.transform.worldToLocalMatrix*camera.transform.localToWorldMatrix;
        Array.Clear(done,0,done.Length);
        void Build(int i){
            if(done[i])return;int parent=Rig.Parents[i];
            if(parent<local.Length&&parent!=i){Build(parent);Pose[i]=Compose(parent,i);}else Pose[i]=matrices[i];
            if(billboardBones[i]!=0)Pose[i]=Billboard(Pose[i],billboardBones[i],cameraLocal);
            done[i]=true;
        }
        for(int i=0;i<Pose.Length;i++)Build(i);
        UpdateMeshes(cameraLocal);
    }
    static Matrix4x4 Billboard(Matrix4x4 placement,int mode,Matrix4x4 camera)
    {
        if(mode<1||mode>3)return placement;
        // Perform basis construction in the original right-handed coordinates.
        var m=Mirror*placement*Mirror;var c=Mirror*camera*Mirror;
        Vector3 position=m.GetColumn(3),direction=(Vector3)c.GetColumn(3)-position;
        if(direction.sqrMagnitude<1e-12f)return placement;
        Vector3 x,y,z;
        if(mode==3){x=-(Vector3)c.GetColumn(0);y=c.GetColumn(1);z=-(Vector3)c.GetColumn(2);}
        else if(mode==2){y=((Vector3)m.GetColumn(1)).normalized;x=Vector3.Cross(y,direction);z=Vector3.Cross(x.normalized,y);}
        else {z=direction.normalized;x=Vector3.Cross(((Vector3)c.GetColumn(1)).normalized,z);y=Vector3.Cross(z,x.normalized);}
        if(x.sqrMagnitude<1e-12f||y.sqrMagnitude<1e-12f||z.sqrMagnitude<1e-12f)return placement;
        void SetAxis(int axis,Vector3 direction){Vector3 v=direction.normalized*((Vector3)m.GetColumn(axis)).magnitude;m.SetColumn(axis,new Vector4(v.x,v.y,v.z,0));}
        SetAxis(0,x);SetAxis(1,y);SetAxis(2,z);
        return Mirror*m*Mirror;
    }
    void UpdateMeshes(Matrix4x4? camera)
    {
        for(int i=0;i<Pose.Length;i++)skin[i]=Pose[i]*inverse[i];
        foreach(var item in skins){
            var rigid=item.Rigid>=0?skin[item.Rigid]:Matrix4x4.identity;
            if(camera.HasValue&&item.Billboard!=0&&item.Palette==null){
                var placed=item.Rigid>=0?Pose[item.Rigid]:Matrix4x4.identity;
                rigid=Billboard(placed,item.Billboard,camera.Value)*(item.Rigid>=0?inverse[item.Rigid]:Matrix4x4.identity);
            }
            for(int v=0;v<item.Vertices.Length;v++){
                Vector3 p=Vector3.zero,n=Vector3.zero;float sum=0;Vector3 tangent=Vector3.zero;
                if(item.Palette!=null)for(int w=0;w<8;w++){
                    float weight=item.Part.BoneWeights[v,w]/65535f;if(weight==0)continue;int bone=item.Palette[item.Part.BoneIndices[v,w]];
                    p+=skin[bone].MultiplyPoint3x4(item.Vertices[v])*weight;n+=skin[bone].MultiplyVector(item.Normals[v])*weight;if(item.Part.Tangents!=null)tangent+=skin[bone].MultiplyVector(item.Part.Tangents[v])*weight;sum+=weight;
                }
                if(sum==0){p=rigid.MultiplyPoint3x4(item.Vertices[v]);n=rigid.MultiplyVector(item.Normals[v]);if(item.Part.Tangents!=null)tangent=rigid.MultiplyVector(item.Part.Tangents[v]);}
                item.Output[v]=p;item.OutputNormals[v]=n.normalized;
                if(item.OutputTangents!=null){tangent.Normalize();item.OutputTangents[v]=new Vector4(tangent.x,tangent.y,tangent.z,-1);}
            }
            if(item.OutputTangents!=null)item.Mesh.tangents=item.OutputTangents;
            item.Mesh.vertices=item.Output;item.Mesh.normals=item.OutputNormals;item.Mesh.RecalculateBounds();
        }
    }
    internal Matrix4x4 Bone(string name)=>Rig.ByHash.TryGetValue(G4TextureParser.Crc32(name),out int i)?Pose[i]:Matrix4x4.identity;
    internal void VisibilityFrame(string cut,float seconds)
    {
        foreach(var item in skins)item.Renderer.enabled=item.Drawable;
        foreach(var bank in VisibilityBanks){var clip=bank.Find(cut);if(clip==null)continue;float frame=clip.Start+seconds*clip.Fps;
            foreach(var track in clip.Tracks)foreach(var curve in track.Curves){if(curve.Type!=15)continue;bool visible=curve.Sample(frame).x!=0;
                foreach(var item in skins)if(item.Part.NodeHash==track.Target||G4TextureParser.Crc32(item.Part.Name)==track.Target)item.Renderer.enabled=visible&&item.Drawable;
            }
        }
        // Native effect material opacity also controls submission, even when
        // blending is disabled. Otherwise a zero-alpha helper with One/Zero
        // blending draws its RGB and writes depth (ev60_00240's dummy backdrop).
        // Evaluate after the material tracks and combine with G4VS visibility;
        // recomputing every frame also restores the mesh after reverse seeks.
        foreach(var item in skins){
            var material=item.Renderer.sharedMaterial;
            if(material.HasProperty("_Family")&&material.color.a<.005f)item.Renderer.enabled=false;
        }
    }
    internal static int NativeDrawKey(int layer)
    {
        layer=Mathf.Clamp(layer,0,63);
        if(layer>=50&&layer<55)return -1; // outside all native draw ranges
        int stage=layer<2?0:layer<10?1:layer==21||layer==39?2:layer<20?3:
            layer<25?4:layer<30?10:layer==37||layer==38?11:layer<37?12:
            layer<50?13:layer<60?20:21;
        return stage*64+layer;
    }
    static int NativeBlend(byte value)=>value switch{0=>0,1=>1,2=>3,3=>6,4=>5,5=>10,6=>7,7=>8,8=>2,9=>4,10=>9,_=>throw new InvalidDataException("Unsupported native effect blend factor "+value)};
    static string UvPrefix(Material material,int slot)=>material.HasProperty("_EventUvRow0")?"_EventUv":slot==0?"_Uv":slot==1?"_Mask":slot==2?"_Extra":"_Tex"+slot;
    internal void BallStatus(Vector4 color,Vector4 rim,float power)
    {
        foreach(var material in materials)if(material.HasProperty("_StatusColor")){
            material.SetVector("_StatusColor",color);material.SetVector("_StatusRim",rim);material.SetFloat("_StatusPower",power);
        }
    }
    internal void MaterialFrame(string cut,float seconds)
    {
        foreach(var material in materials){material.mainTextureOffset=uvOffsets[material];material.color=colors[material];}
        foreach(var entry in uvRows)for(int slot=0;slot<entry.Value.Length/2;slot++){string prefix=UvPrefix(entry.Key,slot);entry.Key.SetVector(prefix+"Row0",entry.Value[slot*2]);entry.Key.SetVector(prefix+"Row1",entry.Value[slot*2+1]);}
        foreach(var entry in ambientColors)entry.Key.SetFloat("_Gain",1+entry.Value.x);
        foreach(var entry in parameters)for(int i=0;i<entry.Value.Length;i++)entry.Key.SetVector("_Param"+i,entry.Value[i]);
        foreach(var bank in MaterialBanks){var clip=bank.Find(cut);if(clip==null&&bank.Clips.Any(c=>System.Text.RegularExpressions.Regex.IsMatch(c.Name,@"^c\d+$")))continue;clip??=bank.Clips.FirstOrDefault();if(clip==null)continue;
            float frame=clip.Start+seconds*clip.Fps;
            foreach(var track in clip.Tracks){
                if(uvTargets.TryGetValue(track.Target,out var targets))foreach(var target in targets){
                    string prefix=UvPrefix(target.material,target.slot),u=prefix+"Row0",v=prefix+"Row1";
                    var rowU=target.material.GetVector(u);var rowV=target.material.GetVector(v);
                    AnimateUv(ref rowU,ref rowV,track,frame);
                    target.material.SetVector(u,rowU);target.material.SetVector(v,rowV);
                }
                foreach(var material in materials.Where(m=>G4TextureParser.Crc32(m.name)==track.Target)){
                foreach(var c in track.Curves){var v=c.Sample(frame);var color=material.color;
                    // G4MA's parameter selects the color register: 0 is diffuse,
                    // 1 is ambient. Treating ambient as diffuse tints rocks cyan
                    // and overwrites opacity with unrelated lighting controls.
                    if(c.Type>=16&&c.Type<=19&&c.Parameter==0){color[c.Type-16]=v.x;material.color=color;}
                    else if(c.Type==16&&c.Parameter==1&&ambientColors.ContainsKey(material))material.SetFloat("_Gain",1+v.x);
                    else if(c.Type>=32&&c.Type<=35&&c.Parameter<8){string name="_Param"+c.Parameter;if(material.HasProperty(name)){var parameter=material.GetVector(name);parameter[c.Type-32]=v.x;material.SetVector(name,parameter);}}
                }
                }
            }
        }
    }
    static void AnimateUv(ref Vector4 rowU,ref Vector4 rowV,G4AnimationBank.Track track,float frame)
    {
        // G4TP animates authoring-space S*R(-angle)*(uv-offset). Native DDS
        // coordinates invert V, so the shader matrix is F*authoring*F, where
        // F(u,v)=(u,1-v). In particular V offset has the opposite sign from U.
        float a=rowU.x,b=-rowU.y,c=-rowV.x,d=rowV.y;
        float tx=rowU.y+rowU.z,ty=1-rowV.y-rowV.z;
        float determinant=a*d-b*c;
        if(Mathf.Abs(determinant)<1e-10f)return;
        float offsetU=(-d*tx+b*ty)/determinant,offsetV=(c*tx-a*ty)/determinant;
        float scaleU=Mathf.Sqrt(a*a+b*b),scaleV=determinant/scaleU;
        float angle=Mathf.Atan2(b,a);bool changed=false;
        foreach(var curve in track.Curves){
            float value=curve.Sample(frame).x;
            switch(curve.Type){
                case 1:if(Mathf.Abs(value)>1e-10f){scaleU=1/value;changed=true;}break;
                case 2:if(Mathf.Abs(value)>1e-10f){scaleV=1/value;changed=true;}break;
                case 8:angle=value;changed=true;break;
                case 10:offsetU=value;changed=true;break;
                case 11:offsetV=value;changed=true;break;
            }
        }
        if(!changed)return;
        float cosine=Mathf.Cos(angle),sine=Mathf.Sin(angle);
        a=scaleU*cosine;b=scaleU*sine;c=-scaleV*sine;d=scaleV*cosine;
        rowU=new Vector4(a,-b,b-a*offsetU-b*offsetV,rowU.w);
        rowV=new Vector4(-c,d,1-d+c*offsetU+d*offsetV,rowV.w);
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
        string setup=Path.ChangeExtension(path,".objbin");
        if(File.Exists(setup)){
            var flag=CfgTable.Read(File.ReadAllBytes(setup),setup).Rows.FirstOrDefault(row=>row.Values.Length>=2&&row.Text(0)=="NonUniformScale");
            cumulativeScale=flag!=null&&flag.Int(1)!=0;
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
