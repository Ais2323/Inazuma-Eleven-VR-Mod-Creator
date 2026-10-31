using System;
using System.Collections.Generic;
using UnityEngine;

// Keep the baseline so unchanged fields (including per-variant positions) survive export.
[Serializable]
public sealed class CharacterIdentitySettings
{
    public string sourceId;
    public int gender, originalGender, affinity, originalAffinity, position, originalPosition, subPosition, originalSubPosition;
    public uint academicYear, originalAcademicYear, sourceSeries, originalSourceSeries, team, originalTeam;
    public uint techniqueParameterId;
    public List<CharacterTechniqueSettings> techniques;
    public List<CharacterLocalizedIdentity> locales = new();
    public CharacterIdentitySettings Clone() => JsonUtility.FromJson<CharacterIdentitySettings>(JsonUtility.ToJson(this));
}

[Serializable]
public sealed class CharacterLocalizedIdentity
{
    public string locale, fullName, shortName, shirtName, description, japaneseName;
    public string originalFullName, originalShortName, originalShirtName, originalDescription;
}

[Serializable]
public sealed class CharacterTechniqueSettings
{
    public uint parameterId;
    public int rarity, affinity;
    public bool hasBuildType;
    public int buildType, originalBuildType;
    public uint[] skills, originalSkills;
    public int[] levels, originalLevels;
    public List<CharacterSkillTransformation> transformations = new();
}

[Serializable]
public sealed class CharacterSkillTransformation
{
    public int slot, category;
    public bool useExistingModel;
    public uint auraId, bindSkill;
    public string characterKey, characterId, characterName, modeName;
    public List<CharacterModeName> modeNames = new();
    public string DisplayName => category == 8 && !string.IsNullOrWhiteSpace(modeName) ? modeName : characterName;
    public string ModeNameForLocale(string locale, string fallback = null)
    {
        var entry = modeNames?.Find(n => n.locale == locale);
        return category != 8 ? characterName : !string.IsNullOrWhiteSpace(entry?.name) ? entry.name :
            !string.IsNullOrWhiteSpace(entry?.defaultName) ? entry.defaultName :
            !string.IsNullOrWhiteSpace(modeName) ? modeName : fallback ?? characterName;
    }
    public void SetModeName(string locale, string value)
    {
        modeNames ??= new List<CharacterModeName>();
        var entry = modeNames.Find(n => n.locale == locale);
        if (entry == null) { entry = new CharacterModeName { locale = locale, defaultName = ModeNameForLocale(locale) }; modeNames.Add(entry); }
        entry.name = string.IsNullOrWhiteSpace(value) ? entry.defaultName : value;
    }
}

[Serializable]
public sealed class CharacterModeName
{
    public string locale, name, defaultName;
}
