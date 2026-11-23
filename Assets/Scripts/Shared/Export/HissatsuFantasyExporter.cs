using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using G4Runtime;
using G4Composition;

// Framework-native declarations: the anim engine copies each selected retail
// cut with its actors, camera, light, sound and effects when building the mod.
internal static class HissatsuFantasyExporter
{
    static string Q(string value)=>Newtonsoft.Json.JsonConvert.ToString(value??"");
    static string Token(string value)
    {
        using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value))).Replace("-","").Substring(0,24).ToLowerInvariant();
    }
    internal static int ParticipantCount(HissatsuEvent ev)
    {
        int count=1;
        foreach(var command in ev.Commands.Where(c=>c.Code==HissatsuEvent.Actor)){
            string actor=command.Text(0)??"";
            if(!actor.StartsWith("c00",StringComparison.Ordinal))continue;
            var match=System.Text.RegularExpressions.Regex.Match(actor,@"_s0([0-3])_");
            if(match.Success)count=Math.Max(count,int.Parse(match.Groups[1].Value)+1);
        }
        return count;
    }
    internal static int CutSeatCount(HissatsuEvent ev,HissatsuEvent.Cut cut)
    {
        int count=0;
        foreach(var command in ev.Commands.Where(c=>c.Code==HissatsuEvent.Motion&&(c.Cut==cut.Name||c.Frame==cut.Start))){
            string actor=command.Text(0)??"";
            if(!actor.StartsWith("c00",StringComparison.Ordinal))continue;
            var match=System.Text.RegularExpressions.Regex.Match(actor,@"_s0([0-3])_");
            if(match.Success)count=Math.Max(count,int.Parse(match.Groups[1].Value)+1);
        }
        return count;
    }
    internal static void Export(IEnumerable<HissatsuMakerDocument> documents,string root,string modNamespace)
    {
        var docs=documents?.ToArray()??Array.Empty<HissatsuMakerDocument>();if(docs.Length==0)return;
        var catalog=new HissatsuCatalog();var nativeIds=new HashSet<uint>(catalog.Entries.Select(e=>e.Id));
        var keys=new HashSet<string>(StringComparer.Ordinal);var replacements=new HashSet<uint>();var generated=new HashSet<uint>();
        var techniques=new StringBuilder("schema = \"VRFW.technique/3\"\n");
        var animations=new StringBuilder("schema = \"VRFW.anim/2\"\nowner = \"match\"\n");
        var shop=new StringBuilder("schema = \"VRFW.shop/2\"\n\n[[edit]]\ntarget = \"InazumaFantasy.technique_shop\"\nif_mod = \"InazumaFantasy\"\n");
        bool hasShop=false;
        var sounds=new StringBuilder("schema = \"VRFW.sound/1\"\n");
        bool hasSounds=false;
        var soundCues=new Dictionary<string,string>(StringComparer.Ordinal);
        var nativeTracks=new HissatsuNativeTracks();
        string SoundCue(string ev){
            if(soundCues.TryGetValue(ev,out var cached))return cached;
            string path=Path.Combine(DumpDataManager.CommonPath,"event_cfg","snd",ev+"_snd.cfg.bin");
            var cues=File.Exists(path)?CfgTable.Read(File.ReadAllBytes(path),path).Rows
                .Where(r=>r.Name=="EVENT_COMMAND_ARGS"&&r.Values.Length>2&&r.Values[1] is long&&r.Int(1)==0)
                .Select(r=>r.Text(2)).Where(c=>c!=null&&c.EndsWith("_me",StringComparison.Ordinal)).Distinct().ToArray():Array.Empty<string>();
            if(cues.Length>1)throw new InvalidDataException("Multiple source sound tracks require explicit editing: "+ev);
            return soundCues[ev]=cues.FirstOrDefault();
        }
        foreach(var doc in docs){
            doc.Validate();
            var source=catalog.Entries.FirstOrDefault(e=>e.Id==doc.sourceSkillId&&e.Resource==doc.sourceResource)
                ??throw new InvalidDataException("Missing skill template: "+doc.sourceResource);
            if(source.Category<0||source.Category>3)throw new InvalidDataException("Export supports shoot, dribble, defense and keeper techniques; summon events are not technique rows: "+source.Resource);
            string token=Token(doc.isNew?"new:"+doc.newKey:"replace:"+source.Resource);
            if(!keys.Add(token)||!doc.isNew&&!replacements.Add(source.Id))throw new InvalidDataException("Duplicate skill save or replacement: "+source.Resource);
            string key="skill_"+token,qualified=modNamespace+"."+key;
            if(doc.isNew){uint id=G4TextureParser.Crc32Utf8(qualified);if(nativeIds.Contains(id)||!generated.Add(id))throw new InvalidDataException("Generated skill ID collision: "+qualified);}
            uint nameId=G4TextureParser.Crc32Utf8(qualified+"_name"),descId=G4TextureParser.Crc32Utf8(qualified+"_description");
            if(!generated.Add(nameId)||!generated.Add(descId))throw new InvalidDataException("Generated skill text ID collision: "+qualified);
            foreach(var sequence in doc.sequences){
                if(sequence.eventName!=source.Event&&sequence.eventName!=source.AlternateEvent)throw new InvalidDataException("Playlist does not belong to skill: "+sequence.eventName);
                sequence.ValidateSource(HissatsuEvent.Read(sequence.eventName));
                foreach(var clip in sequence.clips.Where(c=>!string.IsNullOrEmpty(c.sourceEvent))){
                    var owner=catalog.Entries.FirstOrDefault(e=>e.Id==clip.sourceSkillId&&e.Resource==clip.sourceResource);
                    if(owner==null||(clip.sourceEvent!=owner.Event&&clip.sourceEvent!=owner.AlternateEvent)||
                        (clip.assignedModel??"")!=(owner.AssignedModel??"")||(clip.assignedPath??"")!=(owner.AssignedPath??""))
                        throw new InvalidDataException("Invalid imported Motion source: "+clip.sourceResource);
                }
            }
            int users=ParticipantCount(HissatsuEvent.Read(source.Event));
            string Event(string native,string suffix){
                var sequence=doc.sequences.FirstOrDefault(s=>s.eventName==native)??HissatsuClipSequence.From(HissatsuEvent.Read(native));
                if(sequence.clips.Count==0)throw new InvalidDataException(source.Resource+": cannot export an empty "+suffix+" animation.");
                string local="anim_"+token+"_"+suffix;
                var skeleton=sequence.clips.Select(c=>HissatsuEvent.Read(c.EventFor(native))).Append(HissatsuEvent.Read(native))
                    .OrderByDescending(ParticipantCount).First();
                int seats=ParticipantCount(skeleton);
                animations.Append("\n[[event]]\nname = ").Append(Q(local)).Append("\nfrom = ").Append(Q(skeleton.Name)).Append('\n');
                users=Math.Max(users,seats);
                animations.Append("seats = ").Append(seats).Append('\n');
                var assigned=sequence.clips.Select(c=>c.assignedModel).Append(source.AssignedModel).Where(s=>!string.IsNullOrEmpty(s)).Distinct().OrderBy(s=>s,StringComparer.Ordinal).ToArray();
                if(assigned.Length>0)animations.Append("assigned = [").Append(string.Join(", ",assigned.Select(Q))).Append("]\n");
                var baseCue=SoundCue(skeleton.Name)??sequence.clips.Select(c=>SoundCue(c.EventFor(native))).FirstOrDefault(c=>c!=null);
                if(baseCue!=null){
                    string cue="cm_"+Token(modNamespace+":"+local)+"_me";
                    animations.Append("sound_cue = ").Append(Q(cue)).Append('\n');
                    sounds.Append("\n[[sfx]]\nadd = ").Append(Q(cue)).Append("\nfrom = ").Append(Q(baseCue)).Append("\nbank = \"waza_stream\"\nsegments = [\n");
                    foreach(var clip in sequence.clips){
                        string sourceCue=SoundCue(clip.EventFor(native));
                        if(sourceCue==null)sounds.Append("  { silence = ").Append(clip.endFrame-clip.startFrame).Append(" },\n");
                        else sounds.Append("  { cue = ").Append(Q(sourceCue)).Append(", frames = [").Append(clip.startFrame).Append(", ").Append(clip.endFrame-1).Append("], bank = \"waza_stream\" },\n");
                    }
                    sounds.Append("]\n");hasSounds=true;
                }
                animations.Append("cuts = [\n");
                foreach(var clip in sequence.clips){
                    string from=clip.EventFor(native);var ev=HissatsuEvent.Read(from);
                    if(ev.Cuts.Count(c=>c.Name==clip.motion)!=1)throw new InvalidDataException("Ambiguous native Motion name: "+from+" / "+clip.motion);
                    var sourceCut=ev.Cuts.Single(c=>c.Name==clip.motion);
                    // Native cuts already contain their model/face/visibility setup.
                    // An identity seats map needlessly retargets their animation banks,
                    // including discrete G4VS channels. Keep those native cuts intact.
                    animations.Append("  { cut = ").Append(Q(clip.motion));
                    if(from!=skeleton.Name){
                        int sourceSeats=CutSeatCount(ev,sourceCut);
                        var slot=skeleton.Cuts.Where(c=>CutSeatCount(skeleton,c)>=sourceSeats)
                            .OrderBy(c=>CutSeatCount(skeleton,c)).FirstOrDefault()
                            ??throw new InvalidDataException("No compatible participant slot: "+from+" / "+clip.motion);
                        animations.Append(", from = ").Append(Q(from)).Append(", slot = ").Append(Q(slot.Name)).Append(", whole = true");
                        if(sourceSeats<seats)animations.Append(", hide = [").Append(string.Join(", ",Enumerable.Range(sourceSeats,seats-sourceSeats).Select(i=>Q("s"+i.ToString("D2"))))).Append("]");
                    }
                    animations.Append(" },\n");
                }
                animations.Append("]\n");
                nativeTracks.Add(doc.isNew?qualified:source.Resource,suffix=="normal"?"event_id_name":"fail_event_id_name",sequence,native);
                return modNamespace+"."+local;
            }
            string normal=Event(source.Event,"normal"),alternate=string.IsNullOrEmpty(source.AlternateEvent)?null:Event(source.AlternateEvent,"alternate");
            techniques.Append(doc.isNew?"\n[[technique]]\nname = "+Q(key)+"\nbase = "+Q(source.Resource)+"\nmanual = true\n":"\n[[edit]]\ntarget = "+Q(source.Resource)+"\n");
            techniques.Append("users = ").Append(users).Append('\n');
            techniques.Append("event = ").Append(Q(normal)).Append('\n');
            if(alternate!=null)techniques.Append("fail_event = ").Append(Q(alternate)).Append('\n');
            if(doc.isNew)
                techniques.Append("name_text = 0x").Append(nameId.ToString("X8")).Append("\ndescription_text = 0x").Append(descId.ToString("X8")).Append('\n');
            else
                techniques.Append("cells = { skillNameId = 0x").Append(nameId.ToString("X8")).Append(", skillDescId = 0x").Append(descId.ToString("X8")).Append(" }\n");
            if(doc.sellInShop){
                hasShop=true;
                shop.Append("\n  [[edit.entry]]\n  technique = ").Append(Q(doc.isNew?qualified:source.Resource))
                    .Append("\n  price = ").Append(doc.freeInShop?0:doc.shopPrice)
                    .Append("\n  currency = \"tk_bb000001\"\n");
            }
            string table=doc.isNew?"technique":"edit";
            foreach(var info in doc.info.OrderBy(i=>i.locale,StringComparer.Ordinal)){
                techniques.Append("\n[").Append(table).Append(".text.").Append(Q(info.locale)).Append("]\nname = ").Append(Q(info.name))
                    .Append("\ndescription = ").Append(Q((info.description??"").Replace("\\n","\n"))).Append('\n');
            }
        }
        Write(root,"content/techniques/hissatsumaker.toml",techniques.ToString());
        Write(root,"content/anims/hissatsumaker.toml",animations.ToString());
        if(hasSounds)Write(root,"content/sounds/hissatsumaker.toml",sounds.ToString());
        if(hasShop)Write(root,"content/shops/hissatsumaker.toml",shop.ToString());
        nativeTracks.Write(root,modNamespace);
    }
    static void Write(string root,string relative,string text){string path=Path.Combine(root,relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,text,new UTF8Encoding(false));}
}
