using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G4Runtime;
using G4Composition;
using UnityEngine;

internal sealed class HissatsuPreview : IDisposable
{
    internal readonly GameObject Root;
    internal readonly HissatsuEvent Event;
    internal readonly List<string> Warnings=new();
    internal float Duration=>Event.Duration;
    readonly Dictionary<string,HissatsuActor> actors=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,G4AnimationBank> motions=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,string> motionNames=new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> missing=new(StringComparer.OrdinalIgnoreCase);
    readonly Dictionary<string,Color> lightColors=new(StringComparer.OrdinalIgnoreCase);
    readonly Camera camera;
    G4AnimationBank cameraBank;
    readonly string assignedModel,assignedPath;
    static string indexedRoot;
    static Dictionary<string,string[]> modelIndex;
    string Expand(string value)=>value?.Replace("<TYPE>","01").Replace("<NO>","01").Replace("<VARIATION>","00").Replace("<ASSIGN>",assignedModel??"<ASSIGN>");
    internal HissatsuPreview(HissatsuEvent ev,Transform parent,Camera camera,string assignedModel=null,string assignedPath=null)
    {
        Event=ev;this.camera=camera;this.assignedModel=assignedModel;this.assignedPath=assignedPath;Root=new GameObject(ev.Name);Root.transform.SetParent(parent,false);Root.SetActive(false);
    }
    internal IEnumerator Load(Func<bool> cancelled)
    {
        cameraBank=G4AnimationBank.Read(File.ReadAllBytes(Event.CameraPath),Event.CameraPath);
        foreach(var command in Event.Commands.Where(c=>c.Code==unchecked((uint)-1463526750))){
            try{
                string manifest=HissatsuEvent.Resolve(command.Text(0));var list=CfgTable.Read(File.ReadAllBytes(manifest),manifest);
                foreach(var entry in list.Rows.Where(r=>r.Name=="EV_LI_CHARA_LIGHT"&&r.Values.Length>=3)){
                    string file=HissatsuEvent.Resolve(entry.Text(2));var parameters=CfgTable.Read(File.ReadAllBytes(file),file);Color color=Color.black;bool found=false;
                    foreach(var p in parameters.Rows.Where(r=>r.Values.Length>=4&&r.Text(0)?.StartsWith("charaLightColor",StringComparison.OrdinalIgnoreCase)==true)){
                        float power=p.Values.Length>4?Convert.ToSingle(p.Values[4]):1;color+=new Color(Convert.ToSingle(p.Values[1]),Convert.ToSingle(p.Values[2]),Convert.ToSingle(p.Values[3]),0)*power;found=true;
                    }
                    if(found){color.a=1;lightColors[entry.Text(1)]=color;}
                }
            }catch(Exception e){Warnings.Add(Event.Name+" / lighting: "+e.Message);}
        }
        var declarations=Event.Commands.Where(c=>c.Code==HissatsuEvent.Actor||c.Code==HissatsuEvent.Model).GroupBy(c=>Expand(c.Text(0))).Select(g=>g.First()).ToArray();
        foreach(var command in declarations){
            if(cancelled())yield break;
            string name=Expand(command.Text(0)),source=Expand(command.Text(1));
            try{
                HissatsuActor actor;
                if(name.StartsWith("c00",StringComparison.OrdinalIgnoreCase))actor=HissatsuActor.Demo(name,Root.transform);
                else{
                    if(command.Text(0)?.Contains("<ASSIGN>")==true&&assignedPath!=null)source=assignedPath;
                    if(string.IsNullOrEmpty(source)||source=="INVARID")throw new InvalidDataException("Missing event model for "+name);
                    if(!source.Contains("/")&&!source.Contains("\\")){
                        if(modelIndex==null||indexedRoot!=DumpDataManager.CommonPath){indexedRoot=DumpDataManager.CommonPath;modelIndex=Directory.GetFiles(Path.Combine(indexedRoot,"chr"),"*.g4pkm",SearchOption.AllDirectories).GroupBy(p=>Path.GetFileNameWithoutExtension(p),StringComparer.OrdinalIgnoreCase).ToDictionary(g=>g.Key,g=>g.ToArray(),StringComparer.OrdinalIgnoreCase);}
                        string direct=Path.Combine(Event.Directory,source+".g4pkm");
                        if(!File.Exists(direct)&&modelIndex.TryGetValue(source,out var candidates))direct=candidates.OrderBy(p=>p,StringComparer.Ordinal).First();
                        source=File.Exists(direct)?direct.Substring(DumpDataManager.CommonPath.TrimEnd('\\','/').Length+1):"chr/"+source+"/"+source+".g4pkm";
                    }
                    // Native actor declarations distinguish character-model
                    // resources (kind 0, including skill props/dragons) from
                    // effect/point rigs (kind 1). File/folder names do not.
                    bool characterModel=command.Code==HissatsuEvent.Actor||command.Number(2)==0;
                    actor=HissatsuActor.Resource(name,source,Root.transform,cumulativeScale:characterModel);
                }
                actors.Add(name,actor);
                Warnings.AddRange(actor.Warnings.Select(w=>name+": "+w));
            }catch(Exception e){Warnings.Add(name+": "+e.Message);}
            Loading.UpdateText(Event.Name+" / "+name);Loading.UpdateProgress(10+actors.Count*35/Math.Max(1,declarations.Length));
            yield return null;
        }
        if(!actors.Keys.Any(n=>n.StartsWith("c00",StringComparison.OrdinalIgnoreCase)))throw new InvalidDataException(Event.Name+": no character actor could be loaded");
        foreach(var cut in Event.Cuts){
            foreach(var actor in actors){
                if(cancelled())yield break;
                if(actor.Key.IndexOfAny(Path.GetInvalidFileNameChars())>=0){if(missing.Add(actor.Key))Warnings.Add(Event.Name+": unresolved actor template "+actor.Key);continue;}
                string path=Path.Combine(Event.Directory,Event.Name+"_"+actor.Key+"_"+cut.Name+".g4pk");
                string clipName=cut.Name;
                var motionCommand=Event.Commands.LastOrDefault(c=>c.Code==HissatsuEvent.Motion&&Expand(c.Text(0))==actor.Key&&(c.Cut==cut.Name||c.Frame==cut.Start));
                if(motionCommand!=null){clipName=motionCommand.Text(1)??cut.Name;string reference=motionCommand.Text(5);if(!string.IsNullOrEmpty(reference)&&reference!="NONE")path=HissatsuEvent.Resolve(Expand(reference).Replace("<CHARA>",actor.Key).Replace("<CUT>",clipName));}
                motionNames[actor.Key+"/"+cut.Name]=clipName;
                if(motionCommand?.Text(5)==null||motionCommand.Text(5)=="NONE"){
                    var loadCommand=Event.Commands.LastOrDefault(c=>c.Code==unchecked((uint)-715982243)&&Expand(c.Text(0))==actor.Key&&c.Text(3)==cut.Name);
                    if(loadCommand!=null)path=HissatsuEvent.Resolve(Expand(loadCommand.Text(1)).Replace("<CHARA>",actor.Key).Replace("<CUT>",clipName));
                }
                if(!File.Exists(path)){if(motionCommand!=null&&actor.Value.EmbeddedMotion==null&&missing.Add(path))Warnings.Add("Missing motion resource: "+path);continue;}
                try{var blocks=G4AnimationBank.Package(File.ReadAllBytes(path),path);if(blocks.TryGetValue("G4MT",out var mt))motions[actor.Key+"/"+cut.Name]=G4AnimationBank.Read(mt,path);
                    motionNames[actor.Key+"/"+cut.Name]=clipName;
                    foreach(string kind in new[]{"G4MA","G4TP"})if(blocks.TryGetValue(kind,out var data))actor.Value.MaterialBanks.Add(G4AnimationBank.Read(data,path+"/"+kind));
                    if(blocks.TryGetValue("G4VS",out var visibility))actor.Value.VisibilityBanks.Add(G4AnimationBank.Read(visibility,path+"/G4VS"));
                }catch(Exception e){Warnings.Add(path+": "+e.Message);}
            }
            Loading.UpdateText(Event.Name+" / "+cut.Name);Loading.UpdateProgress(45+(Event.Cuts.IndexOf(cut)+1)*45/Event.Cuts.Count);yield return null;
        }
        var known=new HashSet<uint>{HissatsuEvent.Actor,HissatsuEvent.Model,HissatsuEvent.Attach,HissatsuEvent.Motion,HissatsuEvent.Visible,
            unchecked((uint)-50052350),unchecked((uint)-1585127052),unchecked((uint)-715982243),unchecked((uint)-1463526750),
            unchecked((uint)-150365506),476339743,unchecked((uint)-1950176284),unchecked((uint)-1648841908),unchecked((uint)-29805182),unchecked((uint)-448728658),
            0xB9FC8079, // cut binding is resolved by the native motion/camera clip directory
            0x664FD679}; // gameplay keeper gauge; no gameplay HUD in this preview
        var disabledFlags=new HashSet<uint>{0xFBBCCB17,0xC5C98AD3,0x92AF8079,0xBD9E73F6,0xE4131379};
        var unknown=Event.Commands.Where(c=>c.Stage==110&&!known.Contains(c.Code)&&c.Args.Length>0&&c.Text(0)!=null&&
            !(disabledFlags.Contains(c.Code)&&c.Number(1)==0)).Select(c=>"0x"+c.Code.ToString("X8")).Distinct().ToArray();
        if(unknown.Length>0)Warnings.Add(Event.Name+": unsupported visual/control commands: "+string.Join(", ",unknown));
        Loading.UpdateProgress(100);
    }
    G4AnimationBank.Clip Motion(string actor,string cut)
    {
        string name=motionNames.TryGetValue(actor+"/"+cut,out string selected)?selected:cut;
        var bank=motions.TryGetValue(actor+"/"+cut,out var loaded)?loaded:actors[actor].EmbeddedMotion;
        if(bank==null)return null;
        var clip=bank.Find(name);if(clip!=null)return clip;
        // Never substitute another cut's pose when an actor is absent in this
        // cut. Generic one-clip looping resources may still supply a default.
        return bank.Clips.Any(c=>Regex.IsMatch(c.Name,@"^c\d+$"))?null:bank.Clips.FirstOrDefault();
    }
    internal void Evaluate(float seconds)
    {
        seconds=Mathf.Clamp(seconds,0,Duration);float frame=seconds*60;
        var cut=Event.At(seconds);float elapsed=(frame-cut.Start)/60;
        foreach(var pair in actors){var actor=pair.Value;actor.Root.transform.localPosition=Vector3.zero;actor.Root.transform.localRotation=Quaternion.identity;actor.Root.transform.localScale=Vector3.one;
            var motion=Motion(pair.Key,cut.Name);actor.Evaluate(motion,elapsed);actor.MaterialFrame(motion?.Name??cut.Name,elapsed);actor.VisibilityFrame(motion?.Name??cut.Name,elapsed);actor.LightTint(lightColors.TryGetValue(cut.Name,out var tint)?tint:Color.white);
            bool visible=Motion(pair.Key,cut.Name)!=null;
            foreach(var c in Event.Commands.Where(c=>c.Code==HissatsuEvent.Visible&&c.Frame<=frame&&Expand(c.Text(0))==pair.Key))visible=c.Number(1)!=0;
            foreach(var c in Event.Commands.Where(c=>c.Frame<=frame&&Expand(c.Text(0))==pair.Key)){
                if(c.Code==476339743)actor.Root.transform.localScale=new Vector3(c.Number(1),c.Number(2),c.Number(3));
                else if(c.Code==unchecked((uint)-150365506))actor.Root.transform.localPosition=new Vector3(c.Number(1),c.Number(2),-c.Number(3));
            }
            actor.Root.SetActive(visible);
        }
        // Resolve the point rigs first, then attached characters/ball/effects.
        var attachments=new Dictionary<string,HissatsuEvent.Command>(StringComparer.OrdinalIgnoreCase);
        var types=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(var c in Event.Commands.Where(c=>c.Frame<=frame&&(string.IsNullOrEmpty(c.Cut)||c.Cut==cut.Name))){
            if(c.Code==unchecked((uint)-29805182)&&c.Text(1)=="TYPE"){types[c.Text(0)]=c.Text(2);continue;}
            if(c.Code==HissatsuEvent.Attach){
                if(c.Text(0)?.Contains("<TYPE>")==true&&types.TryGetValue(c.Text(0),out string type)&&type!="01")continue;
                attachments[Expand(c.Text(0))]=c;
            }
        }
        var resolved=new HashSet<string>();
        void Attach(string key,HashSet<string> visiting){
            if(resolved.Contains(key)||!actors.TryGetValue(key,out var child)||!attachments.TryGetValue(key,out var c))return;
            if(!visiting.Add(key))throw new InvalidDataException(Event.Name+": cyclic actor attachment "+key);
            string source=Expand(c.Text(1));
            if(actors.TryGetValue(source,out var parent)){
                Attach(source,visiting);var matrix=parent.Root.transform.localToWorldMatrix*parent.Bone(c.Text(2));
                // Placement is a complete transform layer outside the animated
                // model. Its scale also affects every internal bone translation,
                // not just an attachment offset or the visible model's size.
                var transform=child.Root.transform;
                var placed=matrix*Matrix4x4.TRS(new Vector3(c.Number(3),c.Number(4),-c.Number(5))+transform.localPosition,transform.localRotation,transform.localScale);
                transform.SetPositionAndRotation(placed.GetColumn(3),placed.rotation);
                var scale=placed.lossyScale;var parentScale=transform.parent==null?Vector3.one:transform.parent.lossyScale;
                for(int axis=0;axis<3;axis++)if(Mathf.Abs(parentScale[axis])>1e-9f)scale[axis]/=parentScale[axis];
                transform.localScale=scale;
            }
            visiting.Remove(key);resolved.Add(key);
        }
        foreach(string actor in actors.Keys)Attach(actor,new HashSet<string>());
        foreach(var actor in actors.Values)actor.Particles?.Evaluate(cut.Name,elapsed,actor);
        var cameraClip=cameraBank.Find(cut.Name);if(cameraClip==null)throw new InvalidDataException(Event.Name+": missing camera cut "+cut.Name);
        Vector3 position=Vector3.zero,target=Vector3.forward;float roll=0,fov=50;
        foreach(var track in cameraClip.Tracks)foreach(var curve in track.Curves){float v=curve.Sample(cameraClip.Start+elapsed*cameraClip.Fps).x;
            if(curve.Type>=22&&curve.Type<=24)position[curve.Type-22]=curve.Type==24?-v:v;
            else if(curve.Type>=26&&curve.Type<=28)target[curve.Type-26]=curve.Type==28?-v:v;
            else if(curve.Type==30)roll=-v*Mathf.Rad2Deg;else if(curve.Type==31)fov=v*Mathf.Rad2Deg;
        }
        camera.transform.position=position;Vector3 forward=target-position;
        if(forward.sqrMagnitude>1e-10f)camera.transform.rotation=Quaternion.LookRotation(forward,Vector3.up)*Quaternion.AngleAxis(roll,Vector3.forward);
        camera.fieldOfView=Mathf.Clamp(fov,1,179);camera.nearClipPlane=.01f;camera.farClipPlane=5000;
    }
    public void Dispose(){if(Root!=null){Root.SetActive(false);CharacterManager.DestroyRuntimeObject(Root);}actors.Clear();motions.Clear();}
}
