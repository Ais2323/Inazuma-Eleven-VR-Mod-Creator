using System;
using System.IO;
using System.Linq;
using G4Composition;

internal static class HissatsuInfoCatalog
{
    internal static HissatsuLocalizedInfo[] Read(HissatsuCatalog.Entry entry)
    {
        string folder=Path.Combine(DumpDataManager.CommonPath,"gamedata","skill");
        var db=new AvatarRecipeParser.Rdbnp(File.ReadAllBytes(CharacterIdentityCatalog.Find(folder,"skill_config_")),"Skill info");
        var list=db.GetList("m_skillInfoList");uint nameId=0,descriptionId=0;
        for(int i=0;i<list.Count;i++){int row=list.Row(i);if(db.U32(row+list.Field("skillID"))!=entry.Id)continue;
            nameId=db.U32(row+list.Field("skillNameId"));descriptionId=db.U32(row+list.Field("skillDescId"));break;}
        if(nameId==0){var row=CharacterIdentityCatalog.Get().AuraTable?.Rows.FirstOrDefault(r=>r.Name=="AURA_CMD_INFO"&&r.UInt(0)==entry.Id);
            if(row!=null){nameId=row.UInt(2);descriptionId=row.UInt(3);}}
        string text=Path.Combine(DumpDataManager.CommonPath,"text");
        return Directory.GetDirectories(text).Where(d=>File.Exists(Path.Combine(d,"skill_text.cfg.bin"))).OrderBy(Path.GetFileName,StringComparer.Ordinal)
            .Select(d=>{
                var table=CfgTable.Read(File.ReadAllBytes(Path.Combine(d,"skill_text.cfg.bin")),"Skill localized info");
                return new HissatsuLocalizedInfo {locale=Path.GetFileName(d),
                    name=table.Rows.FirstOrDefault(r=>r.Name=="NOUN_INFO"&&r.UInt(0)==nameId&&r.Int(1)==0)?.Text(5)??entry.Label,
                    description=table.Rows.FirstOrDefault(r=>r.Name=="TEXT_INFO"&&r.UInt(0)==descriptionId)?.Text(2)??""};
            }).ToArray();
    }
}
