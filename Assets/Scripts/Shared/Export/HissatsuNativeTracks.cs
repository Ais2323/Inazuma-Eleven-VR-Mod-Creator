using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using G4Runtime;

// Keep discrete native face/mesh channels out of the framework's float resampler.
// Full source cuts are the only intervals currently supported by the editor.
internal sealed class HissatsuNativeTracks
{
    readonly List<string> events=new();
    static string Q(string s)=>Newtonsoft.Json.JsonConvert.ToString(s);
    static IEnumerable<string> Packages(HissatsuEvent ev,string cut)
    {
        var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string pattern){
            if(string.IsNullOrEmpty(pattern)||!pattern.EndsWith(".g4pk",StringComparison.OrdinalIgnoreCase))return;
            pattern=System.Text.RegularExpressions.Regex.Replace(pattern,@"<[^>]+>","*");
            string full=HissatsuEvent.Resolve(pattern),dir=Path.GetDirectoryName(full);
            if(Directory.Exists(dir))foreach(var p in Directory.GetFiles(dir,Path.GetFileName(full)))paths.Add(p);
        }
        Add(Path.Combine(ev.Directory,ev.Name+"_*_"+cut+".g4pk"));
        foreach(var command in ev.Commands){
            if(command.Code==HissatsuEvent.Motion&&command.Text(1)==cut)Add(command.Text(5)?.Replace("<CUT>",cut));
            if(command.Code==0xD552FA5D&&command.Text(3)==cut)Add(command.Text(1)?.Replace("<CUT>",cut));
        }
        return paths.OrderBy(p=>p,StringComparer.Ordinal);
    }
    internal void Add(string skill,string field,HissatsuClipSequence sequence,string native)
    {
        var packs=new List<string>();
        for(int i=0;i<sequence.clips.Count;i++){
            var clip=sequence.clips[i];var ev=HissatsuEvent.Read(clip.EventFor(native));
            string output="c"+((i+1)*10).ToString("D4");
            foreach(string path in Packages(ev,clip.motion)){
                var blocks=G4AnimationBank.Package(File.ReadAllBytes(path),path);var banks=new List<string>();
                foreach(string kind in new[]{"G4MA","G4VS","G4TP"}){
                    if(!blocks.TryGetValue(kind,out var bytes))continue;
                    var bank=G4AnimationBank.Read(bytes,path+"/"+kind);
                    if(bank.Clips.Count!=1||bank.Clips[0].Name!=clip.motion)
                        throw new InvalidDataException("Native side bank must contain its full source cut: "+path+"/"+kind);
                    bytes=(byte[])bytes.Clone();
                    // All generated cut names have the same length. Keep keys,
                    // codecs, integer values and duplicate step keys byte-exact.
                    var b=new G4ModelParser.Binary(bytes,path);int header=b.U16(4);
                    int hashes=header+b.U16(0x2a)*4,names=header+b.U16(0x2e)*4;
                    int keys=header+(b.U16(0x30)<<(b.U8(0x36)*2))*4;
                    Buffer.BlockCopy(BitConverter.GetBytes(G4TextureParser.Crc32(output)),0,bytes,hashes,4);
                    var old=Encoding.ASCII.GetBytes(clip.motion);var replacement=Encoding.ASCII.GetBytes(output);bool renamed=false;
                    for(int at=names;at+old.Length<keys;at++){
                        if(bytes[at+old.Length]!=0||!old.SequenceEqual(bytes.Skip(at).Take(old.Length)))continue;
                        Buffer.BlockCopy(replacement,0,bytes,at,replacement.Length);renamed=true;break;
                    }
                    if(!renamed)throw new InvalidDataException("Missing side-bank clip name: "+path);
                    banks.Add(kind+"="+Q(BitConverter.ToString(bytes).Replace("-","").ToLowerInvariant()));
                }
                if(banks.Count==0)continue;
                string name=Path.GetFileNameWithoutExtension(path);
                var match=System.Text.RegularExpressions.Regex.Match(name,@"^ev\d+_\d+(?:_\d+)?_(.+)_c\d{4}$");
                if(!match.Success)throw new InvalidDataException("Unknown native animation pack name: "+path);
                string actor=match.Groups[1].Value;
                packs.Add("{actor="+Q(actor)+",cut="+Q(output)+",banks={"+string.Join(",",banks)+"}}");
            }
        }
        if(packs.Count>0)events.Add("{skill="+Q(skill)+",field="+Q(field)+",packs={\n"+string.Join(",\n",packs)+"\n}}");
    }
    internal void Write(string root,string mod)
    {
        if(events.Count==0)return;
        string path=Path.Combine(root,"scripts","hissatsumaker_native_tracks.lua");Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllText(path,"-- Generated native face/material/visibility tracks for this Mod only.\nlocal owner="+Q(mod)+"\nlocal events={\n"+string.Join(",\n",events)+"\n}\n"+Runtime,new UTF8Encoding(false));
    }
    internal const string Runtime=@"
local info,warn=VRFW.logger('hissatsumaker_native_tracks')
local function u16(s,p)
  local a,b=s:byte(p+1,p+2); assert(b,'truncated u16'); return a+b*256
end
local function u32(s,p)
  local a,b,c,d=s:byte(p+1,p+4); assert(d,'truncated u32'); return a+b*256+c*65536+d*16777216
end
local function le(n,w)
  local s={}; for i=1,w do s[i]=string.char(n%256); n=math.floor(n/256) end; return table.concat(s)
end
local function put(s,p,v) return s:sub(1,p)..v..s:sub(p+#v+1) end
local function unhex(s) return (s:gsub('%x%x',function(h) return string.char(tonumber(h,16)) end)) end
local function blocks(s)
  assert(s:sub(1,4)=='G4PK','not a G4PK')
  local t,n=u16(s,4),u32(s,32); assert(n>0 and n<256 and t+n*8<=#s,'invalid package table')
  local out={}
  for i=0,n-1 do
    local at,size=t+u32(s,t+i*4)*4,u32(s,t+(n+i)*4)
    assert(size>=4 and at+size<=#s,'invalid package block')
    out[s:sub(at+1,at+4)]={at=at,size=size,index=i}
  end
  return out,t,n
end
local function relocate(bank,target)
  assert(u16(bank,32)==1 and u16(target,32)==1,'expected one side-bank clip')
  local h,th=u16(bank,4),u16(target,4)
  local a,b=u16(bank,h),u16(bank,h+2)
  local ta,tb=u16(target,th),u16(target,th+2)
  assert(b-a==tb-ta,'side-bank interval differs from the full source cut')
  local delta=ta-a
  if delta==0 then return bank end
  bank=put(bank,h,le(ta,2)..le(tb,2))
  local shift=bank:byte(55)
  local channels=h+u16(bank,38)*2^shift*4
  local keys=h+u16(bank,48)*2^(shift*2)*4
  local infos=h+u16(bank,36)*2^shift*4
  local first=u16(bank,h+4)+bank:byte(h+11)*65536
  local changed={}
  for i=first,first+u16(bank,h+6)-1 do
    local at=infos+i*8
    local ch=u16(bank,at+2)+bank:byte(at+6)*65536
    for j=ch,ch+bank:byte(at+5)-1 do
      local c=channels+j*20
      for k=u32(bank,c+8),u32(bank,c+8)+u32(bank,c+16)-1 do
        if not changed[k] then
          local p=keys+k*2;local v=u16(bank,p)+delta
          assert(v>=0 and v<=65535,'side-bank key out of range')
          bank=put(bank,p,le(v,2));changed[k]=true
        end
      end
    end
  end
  return bank
end
local function patch(bytes,replacements)
  local bs,t,n=blocks(bytes);local original=bytes;local added={};local length=#bytes
  for _,kind in ipairs({'G4MA','G4VS','G4TP'}) do
    local hex,b=replacements[kind],bs[kind]
    if hex and b then
      local old=original:sub(b.at+1,b.at+b.size)
      local native=relocate(unhex(hex),old)
      if native~=old then
        local pad=(64-length%64)%64
        added[#added+1]=string.rep(string.char(0),pad)..native
        length=length+pad
        bytes=put(bytes,t+b.index*4,le((length-t)/4,4))
        bytes=put(bytes,t+(n+b.index)*4,le(#native,4))
        length=length+#native
      end
    end
  end
  if #added==0 then return original end
  bytes=put(bytes,12,le(length-u16(bytes,4),4))..table.concat(added)
  blocks(bytes);return bytes
end
local function install()
  assert(VRFW.files and VRFW.files.serve and VRFW.files.redirect,'runtime file serving unavailable')
  local by={}
  for _,r in ipairs(VRFW.table.rows('skill/skill')) do by[VRFW.ids.unsigned(r.skill_id)]=r end
  local count=0
  for _,event in ipairs(events) do
    local id=VRFW.technique(event.skill)
    local row=by[VRFW.ids.unsigned(id)]
    local name=row and row[event.field]
    assert(type(name)=='string' and name:match('^ev%d+_%d+$'),'missing exported event: '..event.skill)
    for _,p in ipairs(event.packs) do
      local file=name..'_'..p.actor..'_'..p.cut..'.g4pk'
      local path='data/common/event/'..name:match('^(ev%d+)_')..'/'..name..'/'..file
      local bytes,why=VRFW.files.game_read(path)
      -- A slot may omit an actor the source cut does not actually play.
      if type(bytes)=='string' then
        local fixed=patch(bytes,p.banks)
        if fixed~=bytes then
          local private='data/common/event/cm_native_tracks/'..owner..'/'..file
          local ok,err=VRFW.files.serve(private,fixed);assert(ok,'serve: '..tostring(err))
          ok,err=VRFW.files.redirect(path,private);assert(ok,'redirect: '..tostring(err))
          count=count+1
        end
      end
    end
  end
  info('preserved native face/material/visibility tracks in '..count..' private animation pack(s)')
end
local ok,why=pcall(install)
if not ok then warn('native animation tracks not installed: '..tostring(why)) end
";
}
