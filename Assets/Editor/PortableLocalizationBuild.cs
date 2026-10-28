using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

// Loose translations are user-editable and are not emitted by Unity's Resources packer.
public sealed class PortableLocalizationBuild : IPostprocessBuildWithReport
{
    public int callbackOrder => 100;
    public void OnPostprocessBuild(BuildReport report)
    {
        switch (report.summary.platform)
        {
            case BuildTarget.StandaloneWindows:
            case BuildTarget.StandaloneWindows64:
            case BuildTarget.StandaloneLinux64:
            case BuildTarget.StandaloneOSX:
                break;
            default:
                return;
        }
        string output = Path.GetDirectoryName(Path.GetFullPath(report.summary.outputPath));
        string target = Path.Combine(output, "Localization");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in new[] { Path.Combine(Application.dataPath, "Resources", "Localization"), Path.Combine(Application.dataPath, "Localization") })
        {
            if (!Directory.Exists(source)) continue;
            foreach (string file in Directory.GetFiles(source, "*.json")) files[Path.GetFileName(file)] = file;
        }
        Directory.CreateDirectory(target);
        foreach (var file in files)
        {
            string destination = Path.Combine(target, file.Key);
            if (!File.Exists(destination)) File.Copy(file.Value, destination);
        }
        // Never ship Assets/setting.json: it contains this developer's personal paths.
        // SettingManager creates setting.json next to the executable on first use.
    }
}
