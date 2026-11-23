using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;

[Serializable]
public sealed class HissatsuMakerDocument
{
    public int version=2;
    public uint sourceSkillId;
    public string sourceResource;
    public bool isNew,alternateAnimation;
    public bool sellInShop,freeInShop;
    public int shopPrice=1;
    public string newKey;
    public List<HissatsuLocalizedInfo> info=new();
    public List<HissatsuClipSequence> sequences=new();
    internal void Validate()
    {
        if((version!=1&&version!=2)||sourceSkillId==0||string.IsNullOrEmpty(sourceResource)||info==null||sequences==null)
            throw new InvalidDataException("Invalid HissatsuMaker document.");
        if(shopPrice<1)throw new InvalidDataException("Shop price must be at least 1; use the free option for a zero price.");
        if(isNew&&string.IsNullOrWhiteSpace(newKey))throw new InvalidDataException("Missing new skill key.");
        if(info.Any(i=>i==null||string.IsNullOrEmpty(i.locale))||info.Select(i=>i.locale).Distinct().Count()!=info.Count)
            throw new InvalidDataException("Invalid or duplicate skill locales.");
        if(sequences.Any(s=>s==null||string.IsNullOrEmpty(s.eventName))||sequences.Select(s=>s.eventName).Distinct().Count()!=sequences.Count)
            throw new InvalidDataException("Invalid or duplicate event sequences.");
        foreach(var s in sequences)s.Validate();
    }
    internal static HissatsuMakerDocument Read(string path)
    {
        var doc=JsonUtility.FromJson<HissatsuMakerDocument>(File.ReadAllText(path));
        if(doc==null)throw new InvalidDataException("Empty HissatsuMaker document.");doc.Validate();doc.Upgrade();return doc;
    }
    internal void Upgrade(){version=2;foreach(var sequence in sequences)foreach(var clip in sequence.clips)if(string.IsNullOrEmpty(clip.instanceId))clip.instanceId=Guid.NewGuid().ToString("N");}
    internal void Write(string path){Validate();Upgrade();File.WriteAllText(path,JsonUtility.ToJson(this,true),new UTF8Encoding(false));}
}
[Serializable]
public sealed class HissatsuLocalizedInfo { public string locale,name,description; }
[Serializable]
public sealed class HissatsuMotionClip
{
    public int sourceIndex,startFrame,endFrame;
    public string motion,instanceId,sourceEvent,sourceResource,assignedModel,assignedPath;
    public uint sourceSkillId;
    public bool neutralIncomingBall;
    internal string EventFor(string fallback)=>string.IsNullOrEmpty(sourceEvent)?fallback:sourceEvent;
    internal HissatsuMotionClip Copy()=>JsonUtility.FromJson<HissatsuMotionClip>(JsonUtility.ToJson(this));
    internal float Duration=>(endFrame-startFrame)/60f;
}
// A playlist references original event intervals. Reordering never rewrites
// absolute native commands, animations or attachments, including reused motions.
[Serializable]
public sealed class HissatsuClipSequence
{
    public string eventName;
    public List<HissatsuMotionClip> clips=new();
    internal float Duration=>clips.Sum(c=>c.Duration);
    internal static HissatsuClipSequence From(HissatsuEvent ev)=>new() {
        eventName=ev.Name,clips=ev.Cuts.Select((c,i)=>new HissatsuMotionClip {instanceId=Guid.NewGuid().ToString("N"),sourceIndex=i,motion=c.Name,startFrame=c.Start,endFrame=c.End}).ToList()
    };
    internal void Validate()
    {
        if(clips==null||clips.Any(c=>c==null||c.sourceIndex<0||string.IsNullOrEmpty(c.motion)||c.endFrame<=c.startFrame||c.startFrame<0))
            throw new InvalidDataException("Invalid motion playlist: "+eventName);
        var ids=clips.Where(c=>!string.IsNullOrEmpty(c.instanceId)).Select(c=>c.instanceId).ToArray();
        if(ids.Distinct().Count()!=ids.Length)throw new InvalidDataException("Duplicate motion instance: "+eventName);
    }
    internal void ValidateSource(HissatsuEvent ev)
    {
        Validate();if(eventName!=ev.Name)throw new InvalidDataException("Wrong playlist event.");
        var sources=new Dictionary<string,HissatsuEvent>{{ev.Name,ev}};
        foreach(var clip in clips){
            string name=clip.EventFor(ev.Name);
            if(!sources.TryGetValue(name,out var source))sources[name]=source=HissatsuEvent.Read(name);
            if(clip.sourceIndex>=source.Cuts.Count)throw new InvalidDataException("Missing source motion: "+name);
            var cut=source.Cuts[clip.sourceIndex];
            if(clip.motion!=cut.Name||clip.startFrame!=cut.Start||clip.endFrame!=cut.End)
                throw new InvalidDataException("Motion source differs from this game data: "+name);
        }
    }
    internal float StartOf(int index)=>clips.Take(index).Sum(c=>c.Duration);
    internal int IndexAt(float time)
    {
        if(clips.Count==0)return -1;float remaining=Mathf.Clamp(time,0,Duration);
        for(int i=0;i<clips.Count;i++){if(remaining<clips[i].Duration||i==clips.Count-1)return i;remaining-=clips[i].Duration;}return -1;
    }
    internal float SourceTime(float time)
    {
        float remaining=Mathf.Clamp(time,0,Duration);
        for(int i=0;i<clips.Count;i++){
            var clip=clips[i];
            if(remaining<clip.Duration||i==clips.Count-1)
                return clip.startFrame/60f+Mathf.Clamp(remaining,0,Mathf.Max(0,clip.Duration-.00001f));
            remaining-=clip.Duration;
        }
        return 0;
    }
    internal int Move(int index,int delta)
    {
        int target=index+delta;if(index<0||index>=clips.Count||target<0||target>=clips.Count)return index;
        var clip=clips[index];clips.RemoveAt(index);clips.Insert(target,clip);return target;
    }
}
