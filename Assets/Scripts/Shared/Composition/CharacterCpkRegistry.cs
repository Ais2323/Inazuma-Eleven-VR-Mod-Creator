using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using UnityEngine;
using G4Runtime;

namespace G4Composition
{
    // A data-only CPK seed for the stock mod manager/Viola. Never replaces its executable.
    public static class CharacterCpkRegistry
    {
        static readonly byte[] Key = {153,173,18,64,186,199,8,67,228,97,39,157,83,92,134,210,31,51,30,189,33,27,219,176,247,243,251,43,52,234,53,86};
        static readonly byte[] Iv = {193,161,228,68,120,240,251,237,240,233,130,136,117,66,85,102};
        [Serializable] sealed class ManagerConfiguration { public string CfgBinPath = null; }
        static int Align(int n) => checked((n + 15) & ~15);
        static bool Plain(byte[] b) => b.Length >= 32 && BitConverter.ToUInt32(b,b.Length-16)==0x62327401;
        static byte[] Crypt(byte[] bytes, bool encrypt)
        {
            using var aes = Aes.Create(); aes.Key=Key; aes.IV=Iv; aes.Mode=CipherMode.CBC; aes.Padding=PaddingMode.PKCS7;
            using var transform=encrypt?aes.CreateEncryptor():aes.CreateDecryptor();
            return transform.TransformFinalBlock(bytes,0,bytes.Length);
        }
        public static string FindSource()
        {
            string native=Path.Combine(Path.GetDirectoryName(DumpDataManager.CommonPath),"cpk_list.cfg.bin");
            if(File.Exists(native)) return native;
            string config=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),".ievrModManager","config.json");
            if(File.Exists(config)) {
                string selected=JsonUtility.FromJson<ManagerConfiguration>(File.ReadAllText(config))?.CfgBinPath;
                if(!string.IsNullOrWhiteSpace(selected)&&File.Exists(selected))return selected;
            }
            throw new FileNotFoundException("新增角色需要原版 cpk_list.cfg.bin。請放入解包資料夾，或先在模組整合器選擇符合遊戲版本的清單。");
        }
        static CfgTable Read(byte[] bytes)
        {
            const string registryHelp = "Cannot decrypt or read the selected CPK registry. Use data/cpk_list.cfg.bin from the currently installed game version; an old registry or another .cfg.bin file is not compatible.";
            byte[] plain;
            try { plain=Plain(bytes)?bytes:Crypt(bytes,false); }
            catch (CryptographicException e) { throw new InvalidDataException(registryHelp, e); }
            if(!Plain(plain))throw new InvalidDataException(registryHelp);
            // Stock Viola can omit the padding before the string block; normalize only that layout.
            int strings=BitConverter.ToInt32(plain,4),length=BitConverter.ToInt32(plain,8);
            if(strings<16||length<0||(long)strings+length>plain.Length-16)throw new InvalidDataException("Invalid CPK string bounds.");
            if(strings!=Align(strings)) {
                int oldNames=Align(checked(strings+length)),newStrings=Align(strings),newNames=Align(checked(newStrings+length));
                if(oldNames>plain.Length-16)throw new InvalidDataException("Invalid CPK name table.");
                var normalized=new byte[checked(newNames+plain.Length-oldNames)];
                Buffer.BlockCopy(plain,0,normalized,0,strings);
                for(int i=strings;i<newStrings;i++)normalized[i]=255;
                Buffer.BlockCopy(plain,strings,normalized,newStrings,length);
                for(int i=newStrings+length;i<newNames;i++)normalized[i]=255;
                Buffer.BlockCopy(plain,oldNames,normalized,newNames,plain.Length-oldNames);
                Buffer.BlockCopy(BitConverter.GetBytes(newStrings),0,normalized,4,4);plain=normalized;
            }
            var table=CfgTable.Read(plain,"CPK registry");
            if(table.Rows.Count<2||table.Rows[0].Name!="CPK_ITEM_BEGIN"||table.Rows.Skip(1).Any(r=>r.Name!="CPK_ITEM"||r.Values.Length!=5))
                throw new InvalidDataException("Unsupported CPK entries.");
            return table;
        }
        static string FullPath(CfgTable.Row row) => row.Text(0)+row.Text(1);
        public static byte[] Build(string source, string existingData, string stagedData)
        {
            var table=Read(File.ReadAllBytes(source)); var marker=table.Rows[0];
            var map=new Dictionary<string,CfgTable.Row>(StringComparer.Ordinal);
            foreach(var row in table.Rows.Skip(1))if(!map.TryAdd(FullPath(row),row))throw new InvalidDataException("Duplicate CPK resource path.");
            var template=table.Rows[1];
            foreach(string root in new[]{existingData,stagedData}.Where(Directory.Exists))
                foreach(string file in Directory.GetFiles(root,"*",SearchOption.AllDirectories)) {
                    string relative=file.Substring(Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar).Length+1).Replace('\\','/');
                    if(!relative.StartsWith("common/",StringComparison.Ordinal)&&!relative.StartsWith("dx11/",StringComparison.Ordinal))continue;
                    string path="data/"+relative;
                    if(!map.TryGetValue(path,out var row)) {
                        row=template.Clone();int slash=path.LastIndexOf('/');row.Values[0]=path.Substring(0,slash+1);row.Values[1]=path.Substring(slash+1);row.Values[2]="";row.Values[3]="";map.Add(path,row);
                    }
                    // Retain native pack locations so disabling the mod still restores vanilla resources.
                    if (string.IsNullOrEmpty(row.Text(2)) && string.IsNullOrEmpty(row.Text(3)))
                        row.Values[4]=checked((long)(int)new FileInfo(file).Length);
                }
            marker.Values[0]=(long)map.Count;table.Rows.Clear();table.Rows.Add(marker);
            table.Rows.AddRange(map.Values.OrderBy(r=>G4TextureParser.Crc32(FullPath(r))));
            byte[] result=Crypt(table.Write(),true); var check=Read(result);
            var keys=check.Rows.Skip(1).Select(r=>G4TextureParser.Crc32(FullPath(r))).ToArray();
            if(check.Rows.Count!=map.Count+1||check.Rows[0].Int(0)!=map.Count||!keys.SequenceEqual(keys.OrderBy(x=>x)))throw new InvalidDataException("CPK registry validation failed.");
            return result;
        }
    }
}