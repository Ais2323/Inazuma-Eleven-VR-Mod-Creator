using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using G4Runtime;

internal sealed class HissatsuCatalog
{
    internal sealed class Entry { internal uint Id; internal int Category; internal string Resource,Event,Label,AssignedModel,AssignedPath; }
    internal readonly List<Entry> Entries=new();
    internal HissatsuCatalog()
    {
        string root=DumpDataManager.CommonPath,folder=Path.Combine(root,"gamedata","skill");
        var events=Directory.GetFiles(Path.Combine(root,"event_cfg","evt"),"*.cfg.bin")
            .Select(p=>Path.GetFileName(p).Replace(".cfg.bin","")).ToDictionary(n=>G4TextureParser.Crc32(n),n=>n);
        var names=CharacterIdentityCatalog.Get().SkillOptions(UniformCatalog.Locale).Where(o=>o.Id!=0).ToDictionary(o=>o.Id,o=>System.Text.RegularExpressions.Regex.Replace(o.Label,@"[\s·．]+[0-9A-Fa-f]{8}$",""));
        string path=CharacterIdentityCatalog.Find(folder,"skill_config_");
        var db=new AvatarRecipeParser.Rdbnp(File.ReadAllBytes(path),path);var list=db.GetList("m_skillInfoList");
        for(int i=0;i<list.Count;i++){
            int row=list.Row(i);uint id=db.U32(row+list.Field("skillID"));int category=db.Byte(row+list.Field("category"))-1;
            if(category<0||category>3)continue;
            string resource=db.String(row+list.Field("skillIDStr"));string ev=db.String(row+list.Field("eventIDName"));
            if(string.IsNullOrEmpty(ev))events.TryGetValue(db.U32(row+list.Field("eventID")),out ev);
            if(string.IsNullOrEmpty(ev))continue; // table placeholders have no preview event
            Entries.Add(new Entry{Id=id,Category=category,Resource=resource,Event=ev,Label=names.TryGetValue(id,out string name)?name:resource});
        }
        // AURA_CMD_INFO[12] is the native summon event, distinct from the
        // normal skill events and from the keshin's armed transformation.
        string auraPath=CharacterIdentityCatalog.Find(folder,"aura_skill_config_");
        var aura=CfgTable.Read(File.ReadAllBytes(auraPath),auraPath);
        string characterFolder=Path.Combine(root,"gamedata","character");
        var bases=CfgTable.Read(File.ReadAllBytes(CharacterIdentityCatalog.Find(characterFolder,"chara_base_")),"Summon character index");
        var models=CfgTable.Read(File.ReadAllBytes(CharacterIdentityCatalog.Find(characterFolder,"chara_model_")),"Summon model index");
        // Special techniques declare their aura actor as <ASSIGN>/INVARID.
        // The owning aura command supplies that character via [6] (technique)
        // and [7] (character), just as it does for the summon event.
        foreach(var entry in Entries){
            var candidates=aura.Rows.Where(r=>r.Name=="AURA_CMD_INFO"&&(r.Int(10)==0||r.Int(10)==3)&&r.UInt(6)==entry.Id)
                .Select(r=>bases.Rows.FirstOrDefault(b=>b.Name=="CHARA_BASE_INFO"&&b.UInt(0)==r.UInt(7)))
                .Where(b=>b!=null).ToArray();
            string eventDirectory=Path.Combine(root,"event",entry.Event.Split('_')[0],entry.Event);
            var actor=candidates.FirstOrDefault(b=>Directory.Exists(eventDirectory)&&Directory.GetFiles(eventDirectory,entry.Event+"_"+b.Text(1)+"_*.g4pk").Length>0)
                ??candidates.FirstOrDefault();
            var model=actor==null?null:models.Rows.FirstOrDefault(m=>m.Name=="CHARA_MODEL_INFO"&&m.UInt(0)==actor.UInt(6));
            if(model!=null){entry.AssignedModel=actor.Text(1);entry.AssignedPath="chr/"+model.Text(1);}
        }
        foreach(var row in aura.Rows.Where(r=>r.Name=="AURA_CMD_INFO"&&r.Int(10)==0)){
            events.TryGetValue(row.UInt(12),out string ev);uint id=row.UInt(0);
            if(string.IsNullOrEmpty(ev))continue;
            var actor=bases.Rows.FirstOrDefault(r=>r.Name=="CHARA_BASE_INFO"&&r.UInt(0)==row.UInt(7));
            var model=actor==null?null:models.Rows.FirstOrDefault(r=>r.Name=="CHARA_MODEL_INFO"&&r.UInt(0)==actor.UInt(6));
            Entries.Add(new Entry{Id=id,Category=4,Resource=row.Text(1),Event=ev,Label=names.TryGetValue(id,out string name)?name:row.Text(1),AssignedModel=actor?.Text(1),AssignedPath=model==null?null:"chr/"+model.Text(1)});
        }
        Entries.Sort((a,b)=>{int c=a.Category.CompareTo(b.Category);return c!=0?c:StringComparer.CurrentCultureIgnoreCase.Compare(a.Label,b.Label);});
    }
}
