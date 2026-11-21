using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using UnityEngine;

namespace G4Runtime
{
    // Native bank layout/quantization cross-checked against G4_Blender's
    // g4mt_probe.py, g4mt_motion.py and g4cm_camera.py. No Python at runtime.
    internal sealed class G4AnimationBank
    {
        internal sealed class Curve
        {
            internal int Type,Parameter;
            internal bool Step;
            internal ushort[] Frames;
            internal Vector4[] Values;
            internal Vector4 Sample(float frame)
            {
                int i=Array.BinarySearch(Frames,(ushort)Mathf.Clamp(Mathf.FloorToInt(frame),0,65535));
                if(i<0)i=~i-1;
                i=Mathf.Clamp(i,0,Frames.Length-1);
                if(Step||i==Frames.Length-1||frame<=Frames[i])return Values[i];
                float t=Mathf.InverseLerp(Frames[i],Frames[i+1],frame);
                if(Type==9){var a=Values[i];var b=Values[i+1];var q=Quaternion.Slerp(new Quaternion(a.x,a.y,a.z,a.w),new Quaternion(b.x,b.y,b.z,b.w),t);return new Vector4(q.x,q.y,q.z,q.w);}
                return Vector4.LerpUnclamped(Values[i],Values[i+1],t);
            }
        }
        internal sealed class Track { internal uint Target; internal readonly List<Curve> Curves=new(); }
        internal sealed class Clip
        {
            internal string Name;
            internal uint Hash;
            internal int Start,End,Fps;
            internal bool Additive;
            internal readonly List<Track> Tracks=new();
            internal float Duration=>(End-Start+1)/(float)Fps;
        }
        internal readonly List<Clip> Clips=new();
        internal Clip Find(string name)=>Clips.FirstOrDefault(c=>c.Name==name||c.Hash==G4TextureParser.Crc32(name));
        internal static Dictionary<string,byte[]> Package(byte[] bytes,string source)
        {
            var b=new G4ModelParser.Binary(bytes,source);var result=new Dictionary<string,byte[]>();
            b.Range(0,4);string magic=System.Text.Encoding.ASCII.GetString(bytes,0,4);
            if(magic!="G4PK"){result[magic]=bytes;return result;}
            int table=b.U16(4),count=b.Int(0x20);b.Range(table,count*8L);
            for(int i=0;i<count;i++){
                int at=checked(table+b.Int(table+i*4)*4),size=b.Int(table+(count+i)*4);b.Range(at,size);if(size<4)continue;
                string kind=System.Text.Encoding.ASCII.GetString(bytes,at,4);
                if(result.ContainsKey(kind))throw new InvalidDataException(source+": duplicate package block "+kind);
                result.Add(kind,bytes.Skip(at).Take(size).ToArray());
            }
            return result;
        }
        internal static G4AnimationBank Read(byte[] bytes,string source)
        {
            var b=new G4ModelParser.Binary(bytes,source);b.Range(0,0x40);
            string magic=System.Text.Encoding.ASCII.GetString(bytes,0,4);
            if(magic!="G4MT"&&magic!="G4CM"&&magic!="G4MA"&&magic!="G4TP"&&magic!="G4VS")throw new InvalidDataException(source+": unsupported animation bank "+magic);
            int header=b.U16(4),shift=b.U8(0x36),count=b.U16(0x20),targetCount=b.U16(0x22);
            if(header!=b.U16(10)*4||shift>3)throw new InvalidDataException(source+": invalid animation header");
            int Section(int f,int bits=0)=>checked(header+(b.U16(f)<<bits)*4);
            int infos=Section(0x24,shift),channels=Section(0x26,shift),scales=Section(0x28),hashes=Section(0x2a),targets=Section(0x2c),nameBase=Section(0x2e),keys=Section(0x30,shift*2),values=Section(0x32,shift*2);
            string[] names=null;
            for(int at=nameBase;at<Math.Min(bytes.Length-count*2,nameBase+Math.Max(256,count*8));at+=2){
                try{var candidate=new string[count];for(int i=0;i<count;i++){int offset=b.U16(at+i*2);if(offset<count*2)throw new InvalidDataException();candidate[i]=b.Text(at+offset);if(candidate[i].Length==0||candidate[i].Any(c=>c<32||c>126))throw new InvalidDataException();}names=candidate;break;}catch(InvalidDataException){}
            }
            if(names==null)throw new InvalidDataException(source+": invalid clip names");
            var bank=new G4AnimationBank();
            for(int i=0;i<count;i++){
                int at=header+i*16;var clip=new Clip{Name=names[i],Hash=b.U32(hashes+i*4),Start=b.U16(at),End=b.U16(at+2),Fps=b.U8(at+9),Additive=(b.U8(at+8)&1)!=0};
                if(clip.Fps==0)clip.Fps=60;
                if(clip.End<clip.Start)throw new InvalidDataException(source+": reversed clip range");
                int first=b.U16(at+4)+(b.U8(at+10)<<16),length=b.U16(at+6);
                for(int n=0;n<length;n++){
                    int info=infos+(first+n)*8,target=b.U16(info);if(target>=targetCount)throw new InvalidDataException(source+": invalid animation target");
                    var track=new Track{Target=b.U32(targets+target*4)};int channel=b.U16(info+2)+(b.U8(info+5)<<16);
                    for(int j=0;j<b.U8(info+4);j++){
                        int c=channels+(channel+j)*20,keyCount=b.Int(c+16);if(keyCount==0)continue;
                        int components=b.U8(c+4),stride=b.U8(c+5),width=b.U8(c+3),codec=b.U8(c+1),keyStart=b.Int(c+8),dataStart=values+b.Int(c+12);
                        if(components<1||components>4||stride<width*components||keyCount>65536)throw new InvalidDataException(source+": invalid curve layout");
                        float scale=b.F32(scales+b.U8(c+6)*4);
                        var curve=new Curve{Type=b.U8(c),Parameter=b.U8(c+7),Step=magic=="G4VS"||b.U8(c+2)==0,Frames=new ushort[keyCount],Values=new Vector4[keyCount]};
                        for(int k=0;k<keyCount;k++){
                            curve.Frames[k]=b.U16(keys+(keyStart+k)*2);if(k>0&&curve.Frames[k]<curve.Frames[k-1])throw new InvalidDataException(source+": unordered curve keys");
                            Vector4 v=Vector4.zero;
                            for(int x=0;x<components;x++){
                                int q=dataStart+k*stride+x*width;
                                if(codec==1&&width==4)v[x]=b.F32(q);
                                else if(codec==1&&width==2)v[x]=b.I16(q);
                                else if(codec==1&&width==1)v[x]=(sbyte)b.U8(q);
                                else if(codec==2&&width==2)v[x]=b.U16(q)*scale/65536f;
                                else if(codec==2&&width==1)v[x]=b.U8(q)*scale/256f;
                                else if(codec==3&&width==2)v[x]=b.I16(q)*scale/32768f;
                                else if(codec==3&&width==1)v[x]=(sbyte)b.U8(q)*scale/128f;
                                else throw new InvalidDataException(source+": unsupported curve codec "+codec+"/"+width);
                            }
                            if(curve.Type==9){var q=new Quaternion(v.x,v.y,v.z,v.w).normalized;v=new Vector4(q.x,q.y,q.z,q.w);}
                            curve.Values[k]=v;
                        }
                        track.Curves.Add(curve);
                    }
                    clip.Tracks.Add(track);
                }
                bank.Clips.Add(clip);
            }
            return bank;
        }
    }
}
