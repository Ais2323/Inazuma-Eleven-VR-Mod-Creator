using System;
using System.IO;

public static class DumpDataManager
{
    public static string CommonPath { get; private set; }
    public static string Dx11Path { get; private set; }
    public static string LastError { get; private set; }
    public static bool ValidateSavedPath() => UpdateRootPath(SettingManager.Settings["path"]);
    public static bool UpdateRootPath(string path)
    {
        CommonPath = Dx11Path = null;
        LastError = null;
        try
        {
            if (string.IsNullOrWhiteSpace(path)) throw new IOException("Select a dump folder.");
            path = Path.GetFullPath(path);
            while (Directory.Exists(Path.Combine(path, "data"))) path = Path.Combine(path, "data");
            string common = Path.Combine(path, "common"), dx11 = Path.Combine(path, "dx11");
            Directory.GetFileSystemEntries(Path.Combine(common, "chr", "_face"));
            Directory.GetFileSystemEntries(dx11);
            SettingManager.UpdateInfo("path", path);
            CommonPath = common;
            Dx11Path = dx11;
            return true;
        }
        catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is ArgumentException || e is System.Security.SecurityException)
        {
            LastError = e.Message;
            return false;
        }
    }
}
