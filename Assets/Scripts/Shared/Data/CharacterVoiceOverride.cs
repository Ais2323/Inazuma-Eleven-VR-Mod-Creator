using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

[Serializable]
public sealed class CharacterVoiceOverride
{
    public string category;
    public int page;
    public uint skillId;
    public string skillResource;
    public bool muted;
    public List<CharacterVoiceFile> files = new List<CharacterVoiceFile>();
}

[Serializable]
public sealed class CharacterVoiceFile
{
    public string fileName, hcaBase64;
}

public static class NormalVoiceCategories
{
    static readonly HashSet<string> avatarCueNumbers = new HashSet<string>(
        Enumerable.Range(1, 17).Where(i => i != 7).Select(i => "gl" + (i * 10).ToString("D3"))
        .Concat(new[] { "gl510", "gl520", "sp010", "sp011", "sp015", "sp020", "sp021", "sp030", "sp040", "sp050", "sp051", "sp100", "sp110", "sp150",
            "bt020", "bt030", "bt040", "bt050", "bt060", "bt070", "bt080", "sh010", "sh011", "sh020", "pa010", "pa020", "pa030", "kp020", "kp030", "fr010", "wa010", "wa020" }), StringComparer.Ordinal);

    public static bool KeepInheritedCue(string suffix) => avatarCueNumbers.Contains(suffix) ||
        suffix == "armed" || suffix == "wmm" || suffix == "wmt" ||
        Regex.IsMatch(suffix, @"^(?:(?:a\d{6}_)?[wr]h[sodk]\d{5}|[ka]\d{6}_inc)(?:_sp_\d+|_\d+)?$");

    public static readonly string[] Keys = {
        "goal", "concede", "skillBegin1", "skillBegin2", "skillBegin3", "roar", "Damage",
        "FocusWin", "FocusLose", "FocusIn", "FocusTouch", "ShootBegin", "WallFail", "pass", "getPass",
        "KeeperPass", "KeeperSuccess", "KeeperFail"
    };

    public static string[] Cues(string category)
    {
        switch (category)
        {
            case "goal": return Enumerable.Range(1, 17).Where(i => i != 7).Select(i => "gl" + (i * 10).ToString("D3")).ToArray();
            case "concede": return new[] { "gl510", "gl520" };
            case "skillBegin1": return new[] { "sp010", "wa010", "wa020" };
            case "skillBegin2": return new[] { "sp011" };
            case "skillBegin3": return new[] { "sp015" };
            case "roar": return new[] { "sp050", "sp051" };
            case "Damage": return new[] { "sp150" };
            case "FocusWin": return new[] { "bt020", "bt060", "bt070", "bt080" };
            case "FocusLose": return new[] { "bt030" };
            case "FocusIn": return new[] { "bt040" };
            case "FocusTouch": return new[] { "bt050" };
            case "ShootBegin": return new[] { "sh010", "sh011", "sh012" };
            case "WallFail": return new[] { "sh020" };
            case "pass": return new[] { "pa010" };
            case "getPass": return new[] { "pa020", "fr010" };
            case "KeeperPass": return new[] { "pa030" };
            case "KeeperSuccess": return new[] { "kp020" };
            case "KeeperFail": return new[] { "kp030" };
            case "armed": return new[] { "armed" };
            case "wmm": return new[] { "wmm" };
            case "wmt": return new[] { "wmt" };
            default:
                if (category != null && category.StartsWith("cue:", StringComparison.Ordinal) &&
                    Regex.IsMatch(category.Substring(4), @"^(wh[sodk]\d{5}|[ka]\d{6}_inc)$")) return new[] { category.Substring(4) };
                throw new ArgumentException("Unknown voice category: " + category);
        }
    }

    public static bool Matches(string category, string suffix)
    {
        var cues = Cues(category);
        if (cues.Contains(suffix)) return true;
        if (!category.StartsWith("cue:", StringComparison.Ordinal)) return false;
        string pattern = Regex.Escape(cues[0]);
        // Co-op / split-line variants and the same technique used while armed.
        return Regex.IsMatch(suffix, @"^(?:a\d{6}_)?" + pattern + @"(?:_sp_\d+|_\d+)?$");
    }
}
