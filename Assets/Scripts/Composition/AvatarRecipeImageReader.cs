using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace G4Composition
{
    /// <summary>Fixed-grid glyph recognition for the game's central character-code grid.</summary>
    public static class AvatarRecipeImageReader
    {
        [Serializable]
        public sealed class Result
        {
            public string[] candidates;
            public string[] glyphs;
            public string error;
        }

        public static async Task<Result> ReadAsync(string imagePath, CancellationToken cancellationToken)
        {
#if UNITY_EDITOR_WIN || UNITY_STANDALONE_WIN
            if (string.IsNullOrWhiteSpace(imagePath) || !File.Exists(imagePath))
                throw new FileNotFoundException("找不到圖片檔案。", imagePath);
            TextAsset script = Resources.Load<TextAsset>("AvatarRecipeOcr.ps1");
            if (script == null) throw new InvalidOperationException("缺少 AvatarRecipeOcr.ps1.txt。");
            TextAsset glyphData = Resources.Load<TextAsset>("AvatarRecipeGlyphs");
            if (glyphData == null) throw new InvalidOperationException("Missing AvatarRecipeGlyphs.json.");
            var matcher = new AvatarRecipeGlyphMatcher(glyphData.text);
            string encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script.text));
            string directory = Path.Combine(Application.temporaryCachePath, "AvatarRecipeOcr", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            string resultPath = Path.Combine(directory, "result.json");
            try
            {
                string json = await Task.Run(() =>
                {
                    var start = new ProcessStartInfo
                    {
                        FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"),
                        Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " + encoded,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true
                    };
                    // Paths are data, never interpolated into PowerShell source.
                    start.EnvironmentVariables["AVATAR_RECIPE_IMAGE"] = Path.GetFullPath(imagePath);
                    start.EnvironmentVariables["AVATAR_RECIPE_RESULT"] = resultPath;
                    using (var process = Process.Start(start))
                    {
                        if (process == null) throw new IOException("無法啟動 圖片文字辨識。");
                        var stderr = process.StandardError.ReadToEndAsync();
                        var stdout = process.StandardOutput.ReadToEndAsync();
                        var timer = Stopwatch.StartNew();
                        try
                        {
                            while (!process.WaitForExit(100))
                            {
                                cancellationToken.ThrowIfCancellationRequested();
                                if (timer.Elapsed.TotalSeconds > 60) throw new TimeoutException("圖片辨識逾時。");
                            }
                            cancellationToken.ThrowIfCancellationRequested();
                            if (!File.Exists(resultPath)) throw new IOException("圖片文字辨識 失敗：" + stderr.GetAwaiter().GetResult());
                            return File.ReadAllText(resultPath, Encoding.UTF8);
                        }
                        finally { if (!process.HasExited) process.Kill(); }
                    }
                }, cancellationToken);
                var result = JsonUtility.FromJson<Result>(json);
                if (result == null) throw new InvalidDataException("圖片文字辨識 沒有回傳結果。");
                if (!string.IsNullOrEmpty(result.error)) throw new InvalidOperationException(result.error);
                string diagnostic = "";
                string code = await Task.Run(() => matcher.Recognize(result.glyphs, cancellationToken, out diagnostic), cancellationToken);
                result.candidates = new[] { code };
                result.error = diagnostic;
                result.glyphs = null;
                return result;
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
#else
            await Task.Yield();
            throw new PlatformNotSupportedException("圖片角色編號辨識目前支援 Windows。");
#endif
        }
    }
}