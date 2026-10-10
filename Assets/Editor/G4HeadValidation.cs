#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Threading;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;
using G4Runtime;

[InitializeOnLoad]
public static class G4HeadValidation
{
    static G4HeadValidation()
    {
        EditorApplication.playModeStateChanged += state =>
        {
            if (state != PlayModeStateChange.EnteredEditMode || !SessionState.GetBool("G4SmokeRunning", false)) return;
            SessionState.SetBool("G4SmokeRunning", false);
            string[] args = Environment.GetCommandLineArgs(); int flag = Array.IndexOf(args, "--g4-smoke");
            if (flag < 0) flag = Array.IndexOf(args, "--g4-composition-smoke");
            string result = flag >= 0 ? Path.Combine(args[flag + 1], "smoke-result.txt") : "";
            bool passed = File.Exists(result) && File.ReadAllText(result).StartsWith("PASS:", StringComparison.Ordinal);
            if (Application.isBatchMode) EditorApplication.Exit(passed ? 0 : 1);
        };
    }
    public static void PlayModeSmoke()
    {
        SessionState.SetBool("G4SmokeRunning", true);
        UnityEditor.SceneManagement.EditorSceneManager.OpenScene("Assets/Scenes/CharacterPreviewDemo.unity");
        EditorApplication.EnterPlaymode();
    }
    [MenuItem("Tools/G4/Validate sample heads")]
    public static async void ValidateSamples()
    {
        var root = Environment.GetEnvironmentVariable("G4_VALIDATION_DUMP");
        if (string.IsNullOrWhiteSpace(root)) root = SettingManager.Settings["path"];
        var output = Environment.GetEnvironmentVariable("G4_VALIDATION_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) output = Path.GetFullPath("Logs/HeadValidation");
        Directory.CreateDirectory(output);
        GameObject cameraObject = null, head = null;
        RenderTexture target = null;
        try
        {
            if (!DumpDataManager.UpdateRootPath(root)) throw new Exception(DumpDataManager.LastError);
            var loader = new HeadModelLoader(); var entries = await loader.GetHeadsAsync();
            ValidateBinaryFailures(Path.Combine(DumpDataManager.CommonPath, "chr", "_face", entries[0].RelativePath));
            var samples = entries.GroupBy(e => e.RelativePath.Split(Path.DirectorySeparatorChar)[0]).Select(g => g.First()).ToArray();
            cameraObject = new GameObject("Validation camera", typeof(Camera));
            var camera = cameraObject.GetComponent<Camera>();
            camera.clearFlags = CameraClearFlags.SolidColor; camera.backgroundColor = new Color(0.1f, 0.12f, 0.16f); camera.fieldOfView = 35;
            target = new RenderTexture(768, 768, 24); camera.targetTexture = target;
            foreach (var entry in samples)
            {
                var result = await loader.LoadHeadAsync(entry); head = result.Root;
                var renderers = head.GetComponentsInChildren<Renderer>();
                Bounds bounds = renderers[0].bounds;
                foreach (var r in renderers.Skip(1)) bounds.Encapsulate(r.bounds);
                head.transform.position -= bounds.center;
                float radius = Mathf.Max(bounds.extents.magnitude, 0.01f);
                camera.transform.position = new Vector3(0, 0, -radius / Mathf.Sin(17.5f * Mathf.Deg2Rad) * 1.1f);
                camera.nearClipPlane = radius * 0.005f; camera.farClipPlane = radius * 20;
                camera.Render();
                var previous = RenderTexture.active; RenderTexture.active = target;
                var image = new Texture2D(768, 768, TextureFormat.RGB24, false);
                image.ReadPixels(new Rect(0, 0, 768, 768), 0, 0); image.Apply();
                File.WriteAllBytes(Path.Combine(output, entry.DisplayName.Replace('/', '_') + ".png"), image.EncodeToPNG());
                RenderTexture.active = previous; UnityEngine.Object.DestroyImmediate(image);
                Debug.Log("G4_VALIDATED " + entry.DisplayName + " meshes=" + renderers.Length + " warnings=" + result.Warnings.Count);
                UnityEngine.Object.DestroyImmediate(head); head = null;
            }
            // Cancellation must never publish a partial root.
            try { await loader.LoadHeadAsync(entries[0], new CancellationToken(true)); throw new Exception("Cancellation was ignored."); }
            catch (OperationCanceledException) { }
            File.WriteAllText(Path.Combine(output, "unity-validation.txt"), "PASS: " + samples.Length + " series rendered; cancellation, corrupt geometry, DDS formats and truncation verified.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }
        catch (Exception e)
        {
            Debug.LogException(e); File.WriteAllText(Path.Combine(output, "unity-validation.txt"), e.ToString());
            if (Application.isBatchMode) EditorApplication.Exit(1);
        }
        finally
        {
            if (head != null) UnityEngine.Object.DestroyImmediate(head);
            if (cameraObject != null) UnityEngine.Object.DestroyImmediate(cameraObject);
            if (target != null) { target.Release(); UnityEngine.Object.DestroyImmediate(target); }
        }
    }
    static void ValidateBinaryFailures(string path)
    {
        byte[] md = File.ReadAllBytes(path), mg = File.ReadAllBytes(Path.ChangeExtension(path, ".g4mg"));
        Reject(() => G4ModelParser.Parse(md.Take(60).ToArray(), mg, path), "truncated G4MD");
        Reject(() => G4ModelParser.Parse(md, mg.Take(20).ToArray(), path), "truncated G4MG");
        var corrupt = (byte[])mg.Clone();
        int firstRecord = BitConverter.ToUInt16(md, 4);
        int index = checked((int)(BitConverter.ToUInt32(md, 0x5C) + BitConverter.ToUInt32(md, firstRecord + 4)));
        corrupt[index] = corrupt[index + 1] = 255;
        Reject(() => G4ModelParser.Parse(md, corrupt, path), "out-of-range triangle");
        foreach (int dxgi in new[] { 71, 77, 98, 28, 87 })
        {
            int pixels = dxgi == 71 ? 8 : dxgi == 28 || dxgi == 87 ? 64 : 16;
            var dds = new byte[148 + pixels];
            System.Text.Encoding.ASCII.GetBytes("DDS ").CopyTo(dds, 0);
            Put(dds, 4, 124); Put(dds, 12, 4); Put(dds, 16, 4); Put(dds, 28, 1); Put(dds, 76, 32); Put(dds, 80, 4);
            System.Text.Encoding.ASCII.GetBytes("DX10").CopyTo(dds, 84);
            Put(dds, 128, dxgi); Put(dds, 132, 3); Put(dds, 140, 1);
            var texture = G4TextureParser.ParseDDS(dds, "synthetic DXGI " + dxgi);
            if (texture.Pixels.Length != pixels || texture.Width != 4 || texture.Height != 4) throw new Exception("DDS size mismatch.");
            Reject(() => G4TextureParser.ParseDDS(dds.Take(dds.Length - 1).ToArray(), "truncated"), "truncated DDS payload");
            Put(dds, 128, 999);
            Reject(() => G4TextureParser.ParseDDS(dds, "unsupported"), "unsupported DXGI");
        }
        Reject(() => G4TextureParser.Parse(new byte[32], "bad G4TX", new System.Collections.Generic.List<string>()), "invalid G4TX");
    }
    static void Put(byte[] data, int offset, int value) => BitConverter.GetBytes(value).CopyTo(data, offset);
    static void Reject(Action action, string name)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new Exception(name + " was incorrectly accepted.");
    }
    public static void BuildWindows()
    {
        string output = Environment.GetEnvironmentVariable("G4_BUILD_OUTPUT");
        if (string.IsNullOrWhiteSpace(output)) output = "Builds/HeadDemo/HeadDemo.exe";
        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { "Assets/Scenes/StartScene.unity", "Assets/Scenes/CharacterPreviewDemo.unity" },
            target = BuildTarget.StandaloneWindows64,
            locationPathName = output,
            options = BuildOptions.Development
        });
        if (report.summary.result != BuildResult.Succeeded) throw new Exception("Windows build failed: " + report.summary.result);
    }
}
#endif
