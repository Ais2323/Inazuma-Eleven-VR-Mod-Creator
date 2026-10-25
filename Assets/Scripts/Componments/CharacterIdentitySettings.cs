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
    public List<CharacterLocalizedIdentity> locales = new();
    public CharacterIdentitySettings Clone() => JsonUtility.FromJson<CharacterIdentitySettings>(JsonUtility.ToJson(this));
}

[Serializable]
public sealed class CharacterLocalizedIdentity
{
    public string locale, fullName, shortName, shirtName, description, japaneseName;
    public string originalFullName, originalShortName, originalShirtName, originalDescription;
}
