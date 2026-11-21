using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using G4Composition;
using UnityEngine;

// Native PTLB emitters, sampled from a fixed seed and absolute event time.
// Restarting simulation makes reverse scrubbing independent of playback history.
internal sealed class HissatsuParticles
{
    sealed class Emitter { internal string Node,Cut; internal ParticleSystem System; }
    readonly List<Emitter> emitters=new();
    internal HissatsuParticles(string path,Transform root,Func<string,Material> material)
    {
        var table=CfgTable.Read(File.ReadAllBytes(path),path);
        var nodes=table.Rows.Where(r=>r.Name=="PARTICLE_NODE_INFO").Select(r=>r.Text(0)).ToArray();
        int index=-1;var sections=new Dictionary<string,CfgTable.Row>();
        void Build(){
            if(index<0||index>=nodes.Length)return;
            float Value(string key,int at,float fallback=0)=>sections.TryGetValue(key,out var row)&&row.Values.Length>at?Convert.ToSingle(row.Values[at]):fallback;
            string node=nodes[index];var go=new GameObject(node+" particles");go.transform.SetParent(root,false);
            var ps=go.AddComponent<ParticleSystem>();ps.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);ps.useAutoRandomSeed=false;ps.randomSeed=G4Runtime.G4TextureParser.Crc32(node);
            var main=ps.main;main.playOnAwake=false;main.loop=false;main.duration=Mathf.Max(.01f,Value("EMITTER_INFO",2)+Value("EMITTER_INFO",3)+Value("EMITTER_INFO",4));
            main.startLifetime=Mathf.Max(.001f,Value("LIFE_TIME_INFO",1,1));main.maxParticles=2048;
            main.startSpeed=new ParticleSystem.MinMaxCurve(Value("SPEED_INFO",1),Mathf.Max(Value("SPEED_INFO",1),Value("SPEED_INFO",2)));
            main.startSize=Mathf.Max(.001f,Value("MODEL_INFO",1,.1f));main.simulationSpace=node.IndexOf("world",StringComparison.OrdinalIgnoreCase)>=0?ParticleSystemSimulationSpace.World:ParticleSystemSimulationSpace.Local;
            var emission=ps.emission;emission.enabled=true;float interval=Value("EMITTER_INFO",0);emission.rateOverTime=interval>0?1/interval:60;
            var shape=ps.shape;shape.enabled=false;
            var color=ps.colorOverLifetime;color.enabled=sections.ContainsKey("COLOR_INFO");
            if(color.enabled){var gradient=new Gradient();gradient.SetKeys(new[]{new GradientColorKey(new Color(Value("COLOR_INFO",1,1),Value("COLOR_INFO",2,1),Value("COLOR_INFO",3,1)),0),new GradientColorKey(new Color(Value("COLOR_INFO",5,1),Value("COLOR_INFO",6,1),Value("COLOR_INFO",7,1)),1)},new[]{new GradientAlphaKey(Value("COLOR_INFO",4,1),0),new GradientAlphaKey(Value("COLOR_INFO",8,0),1)});color.color=new ParticleSystem.MinMaxGradient(gradient);}
            var renderer=go.GetComponent<ParticleSystemRenderer>();renderer.renderMode=ParticleSystemRenderMode.Billboard;
            if(!sections.TryGetValue("MATERIAL_INFO",out var info)||info.Values.Length<2)throw new InvalidDataException(path+": missing particle material");
            renderer.sharedMaterial=material(info.Text(1));
            var cut=Regex.Match(node,@"(c\d{4})$");emitters.Add(new Emitter{Node=node,Cut=cut.Success?cut.Value:null,System=ps});
        }
        foreach(var row in table.Rows){
            if(row.Name=="PARTICLE_INFO_BGN"){index++;sections.Clear();}
            else if(row.Name=="PARTICLE_INFO_END")Build();
            else if(index>=0)sections[row.Name]=row;
        }
        if(index+1!=nodes.Length)throw new InvalidDataException(path+": particle block count mismatch");
    }
    internal void Evaluate(string cut,float elapsed,HissatsuActor actor)
    {
        foreach(var emitter in emitters){bool visible=emitter.Cut==null||emitter.Cut==cut;emitter.System.gameObject.SetActive(visible);if(!visible)continue;
            var bone=actor.Bone(emitter.Node);emitter.System.transform.localPosition=bone.GetColumn(3);emitter.System.transform.localRotation=bone.rotation;
            emitter.System.Simulate(Mathf.Max(0,elapsed),false,true,true);emitter.System.Pause(false);
        }
    }
}
