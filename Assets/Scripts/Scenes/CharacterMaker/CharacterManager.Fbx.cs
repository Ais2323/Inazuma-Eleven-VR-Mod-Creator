using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using G4Composition;
using UnityEngine;

public partial class CharacterManager
{
    internal UniformFbxModel GetFbx(int option) => option == 1 ? nowCharacterData.headFbx : option == 2 ? nowCharacterData.hairFbx : nowCharacterData.otherFbx;
    internal void SetFbx(int option, UniformFbxModel value) {
        if(option==1)nowCharacterData.headFbx=value;else if(option==2)nowCharacterData.hairFbx=value;else if(option==3)nowCharacterData.otherFbx=value;
    }
    internal CharacterMakerPart FbxPart(int option) {
        if(option<1||option>3||!TryGetOptionSelection(option,out string id,out var parts)||parts==null||!parts.Any(t=>t!=null))
            throw new InvalidOperationException(LocalizationManager.GetText("CharacterFbxSelectModel"));
        return MakeSavedPart(option,id);
    }
    internal CharacterMakerPart FbxImportPart(int option) {
        if(FbxSelected(option).Any(t=>t!=null))return FbxPart(option);
        if(option<1||option>3)throw new ArgumentOutOfRangeException(nameof(option));
        // Templates supply native material/expression slots, not a prerequisite
        // model selection. Do not instantiate or change the character until Apply.
        foreach(string id in new[]{nowCharacterData.TemplateId,option==3?"c01000300":"c01000010"}.Distinct()) {
            var part=new CharacterMakerPart {sourceId=id};
            var asset=CharacterFbxAsset.Read(part,false);
            var slots=option==1
                ?asset.Preview.Parts.Where(p=>p.Name.StartsWith("eye_",StringComparison.OrdinalIgnoreCase)||p.Name.StartsWith("mouth_",StringComparison.OrdinalIgnoreCase))
                :asset.Preview.Parts.Where(p=>!p.Name.StartsWith("eye_",StringComparison.OrdinalIgnoreCase)&&!p.Name.StartsWith("mouth_",StringComparison.OrdinalIgnoreCase)).Skip(option==3?1:0).Take(1);
            part.meshNames=slots.Select(p=>p.Name).ToList();
            if(part.meshNames.Count>0)return part;
        }
        throw new InvalidDataException(LocalizationManager.GetText("CharacterFbxSelectModel"));
    }
    internal CharacterMakerPart FbxExportPart(int option) {
        if(FbxSelected(option).Any(t=>t!=null))return FbxPart(option);
        var part=new CharacterMakerPart {sourceId=nowCharacterData.TemplateId};
        var asset=CharacterFbxAsset.Read(part,false);
        var slots=option==1
            ?asset.Preview.Parts.Where(p=>p.Name.StartsWith("eye_",StringComparison.OrdinalIgnoreCase)||p.Name.StartsWith("mouth_",StringComparison.OrdinalIgnoreCase))
            :asset.Preview.Parts.Where(p=>!p.Name.StartsWith("eye_",StringComparison.OrdinalIgnoreCase)&&!p.Name.StartsWith("mouth_",StringComparison.OrdinalIgnoreCase)).Skip(option==3?1:0).Take(1);
        part.meshNames=slots.Select(p=>p.Name).ToList();
        if(part.meshNames.Count==0)part.sourceId=null;
        return part;
    }
    internal Transform[] FbxSelected(int option) {
        TryGetOptionSelection(option,out _,out var parts);return parts??Array.Empty<Transform>();
    }
    internal RawHeadAsset FbxCompleteHead() {
        var doc=new CharacterMakerDocument { version=16,targetId=nowCharacterData.TemplateId,identity=nowCharacterData.identity?.Clone(),
            head=FbxExportPart(1),hair=FbxExportPart(2),other=FbxExportPart(3),
            bodyType=nowCharacterData.bodyType,bodyVariantId=nowCharacterData.bodyVariantId,bodyAvatarCode=nowCharacterData.bodyAvatarCode,
            chestSize=nowCharacterData.chestSize,uniformHem=(nowCharacterData.bodyDetailFlags>>16)&0xffff,uniformSleeve=nowCharacterData.uniformSleeve };
        foreach(var entry in importedTextures) {
            int separator=entry.Key.IndexOf(':');
            if(separator<0||!int.TryParse(entry.Key.Substring(0,separator),out int partOption))continue;
            doc.textures.Add(new CharacterMakerTexture {option=partOption,sourceId=entry.Value.SourceId,role=entry.Value.Role,
                avatarAtlasVersion=entry.Value.AvatarAtlasVersion,ddsBase64=Convert.ToBase64String(entry.Value.Dds)});
        }
        var built=new CharacterMakerBatchExporter().Build(doc);
        var asset=RawHeadAsset.Read(DumpDataManager.CommonPath,DumpDataManager.Dx11Path,built.TargetRelativePath,System.Threading.CancellationToken.None,built.Metadata,built.Geometry);
        asset.TextureContainer=built.Textures;asset.Textures.Clear();asset.Textures.AddRange(RawTexture.Read(built.Textures,"Complete head FBX"));
        // Follow the chosen body's attachment point in both geometry and FBX bind anchors.
        Vector3 sourceAnchor=!string.IsNullOrEmpty(doc.head.avatarCode)&&(doc.head.fbx==null||doc.head.fbx.IsEmpty)?Vector3.zero:HeadAttachmentAnchor.Get(CharacterFbxAsset.Read(doc.head));
        Vector3 targetAnchor=built.Document.head.exportPosition-doc.head.position+Quaternion.Euler(doc.head.rotation)*Vector3.Scale(sourceAnchor,doc.head.scale);
        Vector3 shift=targetAnchor-HeadAttachmentAnchor.Get(asset);var original=CharacterFbxAsset.Rig(asset);
        var rig=new G4Skeleton {Names=original.Names,Hashes=original.Hashes,Parents=original.Parents,Evidence=original.Evidence,
            Bind=original.Bind.Select(b=>Matrix4x4.Translate(new Vector3(shift.x,shift.y,-shift.z))*b).ToArray()};
        rig.InverseBind=rig.Bind.Select(b=>b.inverse).ToArray();foreach(var entry in original.ByHash)rig.ByHash.Add(entry.Key,entry.Value);asset.Skeleton=rig;
        return asset;
    }
    internal void ApplyFbx(int option, CharacterMakerPart part) {
        var warnings = new List<string>();
        var textures = importedTextures.Where(p=>p.Key.StartsWith(option+":",StringComparison.Ordinal)).Select(p=>new CharacterMakerTexture {
            option=option,sourceId=p.Value.SourceId,role=p.Value.Role,avatarAtlasVersion=p.Value.AvatarAtlasVersion,ddsBase64=Convert.ToBase64String(p.Value.Dds)
        }).ToList();
        if(!RestoreSavedModel(option,part.sourceId,warnings,part.avatarCode,part.fbx))throw new InvalidDataException(string.Join("\n",warnings));
        RestoreSavedTransform(option,part,warnings); RestoreSavedTextures(textures,warnings);
        RefreshTransformInputs(option);RefreshTextureImages(option);RefreshPageAvailability();
        if(warnings.Count>0)AppLog.Warning(string.Join("\n",warnings));
    }
    internal GameObject FbxPreview(int option, CharacterMakerPart part, RawHeadAsset asset) {
        var root=CharacterFbxAsset.Preview(asset,part.sourceId);
        root.transform.SetParent(nowCharacterData.nowShowParent,false);
        Vector3 anchor=HeadAttachmentAnchor.Get(asset);
        root.transform.localPosition=(hasTargetFaceAnchor?targetFaceAnchor:anchor)+part.position-Quaternion.Euler(part.rotation)*Vector3.Scale(anchor,part.scale);
        root.transform.localRotation=Quaternion.Euler(part.rotation);root.transform.localScale=part.scale;
        var names=new HashSet<string>(part.meshNames,StringComparer.OrdinalIgnoreCase);
        foreach(Transform child in root.transform)child.gameObject.SetActive(names.Contains(child.name));
        return root;
    }
}
