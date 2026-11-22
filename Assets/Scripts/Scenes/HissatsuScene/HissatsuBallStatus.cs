using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using UnityEngine;

// Native CStatusEffComponent curves are sampled from absolute event time so
// seeking backwards and switching cached previews cannot retain a ball tint.
internal sealed class HissatsuBallStatus
{
    internal const uint Command = 0xC3FB5CC5;
    sealed class Definition
    {
        internal uint[] Curves;
        internal float Duration, FadeIn, FadeOut;
        internal bool Loop;
        internal int Priority;
    }
    sealed class Interval
    {
        internal string Actor;
        internal Definition Definition;
        internal float Start, End = float.PositiveInfinity;
    }
    readonly List<Interval> intervals = new();
    readonly Dictionary<uint,List<(float time,Vector4 value)>> curves = new();

    Definition incoming;
    internal HissatsuBallStatus(HissatsuEvent ev, Func<string,string> expand,bool neutralIncomingBall=false)
    {
        if (!neutralIncomingBall && !ev.Commands.Any(c => c.Code == Command)) return;
        string directory = Path.Combine(DumpDataManager.CommonPath,"effect");
        string Latest(string pattern) => Directory.GetFiles(directory,pattern).OrderBy(p=>p,StringComparer.Ordinal).Last();
        var definitions = new Dictionary<string,Definition>(StringComparer.Ordinal);
        foreach (var row in CfgTable.Read(File.ReadAllBytes(Latest("effect_status_define_*.cfg.bin")),"Ball status definitions").Rows)
            if (row.Hash == 0xAD401CFD && row.Values.Length > 19 && row.Text(0) != null)
                definitions[row.Text(0)] = new Definition {
                    Curves=Enumerable.Range(1,5).Select(row.UInt).ToArray(),
                    Duration=Convert.ToSingle(row.Values[6]),FadeIn=Convert.ToSingle(row.Values[15]),
                    FadeOut=Convert.ToSingle(row.Values[16]),Loop=row.Int(18)==0,Priority=(int)row.Int(17)
                };
        if(neutralIncomingBall)definitions.TryGetValue("ball_aura_05",out incoming);
        List<(float time,Vector4 value)> current = null;
        foreach (var row in CfgTable.Read(File.ReadAllBytes(Latest("effect_define_*.cfg.bin")),"Ball status curves").Rows) {
            if (row.Hash == 0x3C57A91D) curves[row.UInt(0)] = current = new();
            else if (row.Hash == 0x5F244B73) current = null;
            else if (row.Hash == 0x74502ABA && current != null && row.Values.Length >= 5)
                current.Add((Convert.ToSingle(row.Values[0]),new Vector4(Convert.ToSingle(row.Values[1]),Convert.ToSingle(row.Values[2]),Convert.ToSingle(row.Values[3]),Convert.ToSingle(row.Values[4]))));
        }
        foreach(var curve in curves.Values)curve.Sort((a,b)=>a.time.CompareTo(b.time));
        var open = new Dictionary<(string actor,string name),Interval>();
        foreach (var c in ev.Commands.Where(c=>c.Code==Command && c.Args.Length>=3)) {
            string actor=expand(c.Text(0)),name=c.Text(1);
            if(actor==null || name==null || !definitions.TryGetValue(name,out var definition))continue;
            if(neutralIncomingBall&&name.StartsWith("ball_aura_",StringComparison.Ordinal)&&incoming!=null)definition=incoming;
            var key=(actor,name);
            if(c.Number(2)==0){
                if(open.TryGetValue(key,out var previous))previous.End=c.Frame/60f;
                var interval=new Interval{Actor=actor,Definition=definition,Start=c.Frame/60f};
                intervals.Add(interval);open[key]=interval;
            } else if(open.TryGetValue(key,out var interval)){interval.End=c.Frame/60f;open.Remove(key);}
        }
    }
    Vector4 Sample(uint hash,float time,Vector4 fallback)
    {
        if(!curves.TryGetValue(hash,out var keys)||keys.Count==0)return fallback;
        if(time<=keys[0].time)return keys[0].value;
        for(int i=1;i<keys.Count;i++)if(time<=keys[i].time)
            return Vector4.Lerp(keys[i-1].value,keys[i].value,Mathf.InverseLerp(keys[i-1].time,keys[i].time,time));
        return keys[keys.Count-1].value;
    }
    internal void Apply(float seconds,Dictionary<string,HissatsuActor> actors)
    {
        foreach(var actor in actors.Values)actor.BallStatus(Vector4.one,new Vector4(1,1,1,0),10);
        if(incoming!=null)foreach(var pair in actors.Where(p=>p.Key.StartsWith("b000",StringComparison.OrdinalIgnoreCase)))
            pair.Value.BallStatus(Sample(incoming.Curves[0],0,Vector4.one),Sample(incoming.Curves[1],0,new Vector4(1,1,1,0)),Sample(incoming.Curves[2],0,new Vector4(10,0,0,0)).x);
        foreach(var group in intervals.Where(i=>seconds>=i.Start).GroupBy(i=>i.Actor)) {
            var interval=group.Where(i=>seconds<End(i)+i.Definition.FadeOut)
                .OrderBy(i=>i.Definition.Priority).ThenBy(i=>i.Start).LastOrDefault();
            if(interval==null||!actors.TryGetValue(group.Key,out var actor))continue;
            var d=interval.Definition;float age=seconds-interval.Start,end=End(interval);
            float weight=d.FadeIn>0?Mathf.Clamp01(age/d.FadeIn):1;
            if(seconds>=end)weight*=d.FadeOut>0?Mathf.Clamp01(1-(seconds-end)/d.FadeOut):0;
            float duration=Mathf.Max(d.Duration,.00001f),phase=d.Loop?Mathf.Repeat(age,duration)/duration:Mathf.Clamp01(age/duration);
            var color=Vector4.Lerp(Vector4.one,Sample(d.Curves[0],phase,Vector4.one),weight);
            var rim=Vector4.Lerp(new Vector4(1,1,1,0),Sample(d.Curves[1],phase,new Vector4(1,1,1,0)),weight);
            float power=Mathf.Lerp(10,Sample(d.Curves[2],phase,new Vector4(10,0,0,0)).x,weight);
            actor.BallStatus(color,rim,power);
        }
    }
    static float End(Interval i)=>i.Definition.Loop?i.End:Mathf.Min(i.End,i.Start+i.Definition.Duration);
}
