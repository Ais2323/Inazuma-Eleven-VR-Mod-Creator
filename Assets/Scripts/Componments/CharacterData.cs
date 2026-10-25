using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Events;

[System.Serializable]
public class CharacterData
{
    public string id;
    public CharacterIdentitySettings identity;
    public string voiceCharacterId, voiceAvatarCode, voiceName, voicePortraitPng;
    public string headId;
    public string headAvatarCode;
    public Transform[] head;
    public string hairId;
    public string hairAvatarCode;
    public Transform[] hair;
    public string otherId;
    public string otherAvatarCode;
    public Transform[] other;

    public Vector3 headExtraPosition = Vector3.zero;
    public Vector3 headExtraRotation = Vector3.zero;
    public Vector3 headExtraScale = Vector3.one;
    public Vector3 headTransformStartPosition = Vector3.zero;
    public Vector3 hairExtraPosition = Vector3.zero;
    public Vector3 hairExtraRotation = Vector3.zero;
    public Vector3 hairExtraScale = Vector3.one;
    public Vector3 hairTransformStartPosition = Vector3.zero;
    public Vector3 otherExtraPosition = Vector3.zero;
    public Vector3 otherExtraRotation = Vector3.zero;
    public Vector3 otherExtraScale = Vector3.one;
    public Vector3 otherTransformStartPosition = Vector3.zero;

    public int originalBodyType = 1;
    public int bodyType = 1;
    public uint bodyVariantId;
    public string bodyAvatarCode;
    public uint originalBodyVariantId;
    public int originalChestSize = -1;
    public int chestSize = -1;
    public int uniformSleeve, originalUniformSleeve;
    public int originalBodyDetailFlags;
    public int bodyDetailFlags;

    public bool BodySettingsChanged => originalBodyType != bodyType ||
        (bodyVariantId != 0 && bodyVariantId != originalBodyVariantId) ||
        originalChestSize != chestSize || originalBodyDetailFlags != bodyDetailFlags || uniformSleeve != originalUniformSleeve;

    public Transform nowShowParent;

    private UnityAction<Transform[]> destroyFunc;
    private Func<Transform, Transform, Transform> Instantiate;

    public CharacterData(UnityAction<Transform[]> destroyFunc, Func<Transform, Transform, Transform> newTr)
    {
        this.destroyFunc = destroyFunc;
        Instantiate = newTr;
    }

    public void SetData(int type, Transform tr)
    {
        switch(type)
        {
            case 0:
                SetCharId(tr);
                break;
            case 1:
                SetHead(tr);
                break;
            case 2:
                SetHair(tr);
                break;
            case 3:
                SetOther(tr);
                break;
        }
    }

    private void SetCharId(Transform tr)
    {
        id = tr.name;
    }

    private void SetHead(Transform tr)
    {
        destroyFunc.Invoke(head);
        headAvatarCode = null;
        headId = tr.name;
        List<Transform> find = new();
        for (int i = 0; i < tr.childCount; i++)
        {
            var t = tr.GetChild(i);
            if (t.name.StartsWith("eye_") || t.name.StartsWith("mouth_"))
            {
                var newT = Instantiate.Invoke(t, nowShowParent);
                newT.name = t.name;
                find.Add(newT);
            }
        }

        head = find.ToArray();
    }

    private void SetHair(Transform tr)
    {
        destroyFunc.Invoke(hair);
        hairAvatarCode = null;
        hairId = tr.name;
        List<Transform> find = new();
        for (int i = 0; i < tr.childCount; i++)
        {
            var t = tr.GetChild(i);
            if (t.name.StartsWith("eye_") || t.name.StartsWith("mouth_"))
                continue;
            var newT = Instantiate.Invoke(t, nowShowParent);
            newT.name = t.name;
            find.Add(newT);
            break;
        }

        hair = find.ToArray();
    }

    private void SetOther(Transform tr)
    {
        destroyFunc.Invoke(other);
        otherAvatarCode = null;
        otherId = null;
        other = Array.Empty<Transform>();
        int nonFaceIndex = 0;
        for (int i = 0; i < tr.childCount; i++)
        {
            Transform part = tr.GetChild(i);
            if (part.name.StartsWith("eye_", StringComparison.OrdinalIgnoreCase) ||
                part.name.StartsWith("mouth_", StringComparison.OrdinalIgnoreCase))
                continue;
            if (nonFaceIndex++ != 1) continue;
            Transform instance = Instantiate.Invoke(part, nowShowParent);
            instance.name = part.name;
            otherId = tr.name;
            other = new[] { instance };
            return;
        }
    }
}
