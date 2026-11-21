using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;

internal sealed class HissatsuEvent
{
    internal sealed class Command { internal int Frame,Stage; internal uint Code; internal object[] Args; internal string Cut; internal string Text(int i)=>i<Args.Length?Args[i] as string:null; internal float Number(int i)=>i<Args.Length?Convert.ToSingle(Args[i]):0; }
    internal sealed class Cut { internal string Name; internal int Start,End; }
    internal readonly List<Command> Commands=new();
    internal readonly List<Cut> Cuts=new();
    internal string Name,Directory,CameraPath;
    internal float Duration=>Cuts.Max(c=>c.End)/60f;
    internal const uint Actor=896822880,Model=2040143546,Attach=2731669826,Motion=2847795607,Visible=2950922317;
    internal static HissatsuEvent Read(string name)
    {
        if(string.IsNullOrEmpty(name)||!System.Text.RegularExpressions.Regex.IsMatch(name,@"^ev\d+_\d+(?:_\d+)?$"))throw new InvalidDataException("Missing or invalid native skill event: "+name);
        var result=new HissatsuEvent{Name=name,Directory=Path.Combine(DumpDataManager.CommonPath,"event",name.Split('_')[0],name)};
        void ReadCommands(string path,bool required){
            if(!File.Exists(path)){if(required)throw new FileNotFoundException("Missing skill event",path);return;}
            var table=CfgTable.Read(File.ReadAllBytes(path),path);Command pending=null;string cut="";
            foreach(var row in table.Rows){
                if(row.Name=="EVENT_COMMAND_HEADER"){pending=new Command{Frame=(int)row.Int(0),Code=row.UInt(1),Stage=(int)row.Int(2)};continue;}
                if(row.Name!="EVENT_COMMAND_ARGS"||pending==null)continue;
                pending.Args=row.Values;
                if(pending.Code==unchecked((uint)-50052350)&&row.Values.Length>=3){result.Cuts.Add(new Cut{Name=row.Text(0),Start=(int)row.Int(1),End=(int)row.Int(2)});}
                if(pending.Code==unchecked((uint)-1585127052))result.CameraPath=Resolve(row.Text(0));
                if(pending.Text(1)=="RESET_DEF")cut=pending.Text(0);
                pending.Cut=cut;result.Commands.Add(pending);pending=null;
            }
        }
        ReadCommands(Path.Combine(DumpDataManager.CommonPath,"event_cfg","evt",name+".cfg.bin"),true);
        ReadCommands(Path.Combine(DumpDataManager.CommonPath,"event_cfg","eff",name+"_eff.cfg.bin"),false);
        if(result.Cuts.Count==0)throw new InvalidDataException(name+": event has no cuts");
        result.Cuts.Sort((a,b)=>a.Start.CompareTo(b.Start));return result;
    }
    internal static string Resolve(string path)
    {
        if(string.IsNullOrEmpty(path))throw new InvalidDataException("Empty event resource path");
        path=path.Replace('\\','/');if(path.StartsWith("data/",StringComparison.OrdinalIgnoreCase))path=path.Substring(5);
        if(path.StartsWith("common/",StringComparison.OrdinalIgnoreCase))path=path.Substring(7);
        string root=Path.GetFullPath(DumpDataManager.CommonPath).TrimEnd('\\','/')+Path.DirectorySeparatorChar;
        string full=Path.GetFullPath(Path.Combine(root,path));
        if(!full.StartsWith(root,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("Event resource outside dump: "+path);
        return full;
    }
    internal Cut At(float seconds)=>Cuts.LastOrDefault(c=>c.Start<=seconds*60)??Cuts[0];
}
