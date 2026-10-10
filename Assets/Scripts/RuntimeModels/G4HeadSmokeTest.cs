#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityEngine.SceneManagement;

// Opt-in development smoke test. Normal launches never create this component.
public sealed class G4HeadSmokeTest : MonoBehaviour
{
    string output, dump;
    float deadline, next;
    int stage, selected, screenshots;
    string expected;
    CharacterPreviewDemo demo;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Initialize()
    {
        string[] args = Environment.GetCommandLineArgs();
        int flag = Array.IndexOf(args, "--g4-smoke");
        if (flag < 0 || flag + 2 >= args.Length) return;
        string output = Path.GetFullPath(args[flag + 1]);
        Directory.CreateDirectory(output);
        // Keep test settings inside the test output; never overwrite the user's settings.
        SettingManager.SettingPath = Path.Combine(output, "test-settings.json");
        SettingManager.UpdateInfo("path", "");
        var runner = new GameObject("Head smoke test").AddComponent<G4HeadSmokeTest>();
        runner.output = output; runner.dump = args[flag + 2]; runner.deadline = Time.realtimeSinceStartup + 180;
        DontDestroyOnLoad(runner.gameObject);
        Application.logMessageReceived += runner.LogError;
    }
    void LogError(string message, string stack, LogType type)
    {
        if (type == LogType.Error || type == LogType.Exception || type == LogType.Assert)
            File.AppendAllText(Path.Combine(output, "errors.txt"), message + "\n" + stack + "\n");
    }
    void Update()
    {
        try
        {
            if (Time.realtimeSinceStartup > deadline) throw new Exception("Timed out at stage " + stage);
            if (Time.realtimeSinceStartup < next) return;
            switch(stage)
            {
                case 0:
                    if (SceneManager.GetActiveScene().name != "StartScene") return;
                    var start = FindFirstObjectByType<StartSceneUIController>();
                    if (start == null) return;
                    if (!string.IsNullOrEmpty(DumpDataManager.CommonPath)) throw new Exception("Invalid path was accepted.");
                    start.dirInputField.text = dump; start.OnChangePath(dump); start.OnClickLoadDump();
                    stage = 1; break;
                case 1:
                    demo = FindFirstObjectByType<CharacterPreviewDemo>();
                    if (demo == null || demo.CurrentHead == null) return;
                    if (SceneManager.GetActiveScene().name != "CharacterPreviewDemo") throw new Exception("Wrong scene.");
                    Capture("first-head");
                    demo.CharacterDropdown.Show(); next = Time.realtimeSinceStartup + 1; stage = 2; break;
                case 2:
                    var list = demo.CharacterDropdown.transform.Find("Dropdown List");
                    if (list == null || !list.gameObject.activeInHierarchy) throw new Exception("Dropdown did not open.");
                    int itemCount = list.GetComponentsInChildren<UnityEngine.UI.Toggle>().Length;
                    if (itemCount != demo.CharacterDropdown.options.Count) throw new Exception("Dropdown items do not match head catalog.");
                    if (list.GetComponent<UnityEngine.UI.ScrollRect>().verticalScrollbar == null) throw new Exception("Dropdown scrollbar is missing.");
                    File.WriteAllText(Path.Combine(output, "dropdown-count.txt"), itemCount.ToString());
                    Capture("dropdown"); demo.CharacterDropdown.Hide();
                    selected = demo.CharacterDropdown.options.FindIndex(o => o.text.StartsWith("11_VICTORY/", StringComparison.Ordinal));
                    if (selected < 0) selected = demo.CharacterDropdown.options.Count - 1;
                    expected = Path.GetFileName(demo.CharacterDropdown.options[selected].text);
                    // Exercise overlapping requests before settling on the selected character.
                    for (int i = 1; i < Math.Min(8, demo.CharacterDropdown.options.Count); i++) demo.CharacterDropdown.value = i;
                    demo.CharacterDropdown.value = selected;
                    stage = 3; break;
                case 3:
                    if (demo.CurrentHead == null || demo.CurrentHead.name != expected) return;
                    Capture("switched-head"); demo.ResetView();
                    SceneManager.LoadScene("StartScene"); stage = 4; break;
                case 4:
                    demo = FindFirstObjectByType<CharacterPreviewDemo>();
                    if (demo == null || demo.CurrentHead == null) return;
                    if (FindObjectsByType<G4Runtime.RuntimeHeadResources>(FindObjectsSortMode.None).Length != 1) throw new Exception("Head roots leaked across scene changes.");
                    SceneManager.LoadScene("CharacterPreviewDemo"); stage = 5; break;
                case 5:
                    demo = FindFirstObjectByType<CharacterPreviewDemo>();
                    if (demo == null || demo.CurrentHead == null) return;
                    File.WriteAllText(Path.Combine(output,"smoke-result.txt"), "PASS: invalid startup -> StartScene, selected dump -> Demo, dropdown, rapid cancellation, head switch, valid startup -> Demo, direct Demo, scene resource cleanup. Captures="+screenshots);
                    Finish(0); break;
            }
        }
        catch(Exception e)
        {
            File.WriteAllText(Path.Combine(output,"smoke-result.txt"), "FAIL: "+e);
            Finish(1);
        }
    }
    void Finish(int code)
    {
        enabled = false;
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(code);
#endif
    }
    void Capture(string name)
    {
        // Batch players do not present a backbuffer. Render the scene and UI explicitly.
        var camera = FindObjectsByType<Camera>(FindObjectsSortMode.None).First(c => c.name == "Preview Camera");
        var canvas = FindObjectsByType<Canvas>(FindObjectsSortMode.None).First(c => c.name == "Preview UI");
        var previousMode = canvas.renderMode; var previousCamera = canvas.worldCamera;
        var childCanvases = canvas.GetComponentsInChildren<Canvas>().Where(c => c != canvas).ToArray();
        var childCameras = childCanvases.Select(c => c.worldCamera).ToArray();
        var childModes = childCanvases.Select(c => c.renderMode).ToArray();
        var previousActive = RenderTexture.active; var previousTarget = camera.targetTexture;
        var target = new RenderTexture(1280, 720, 24);
        var image = new Texture2D(1280, 720, TextureFormat.RGB24, false);
        try
        {
            camera.targetTexture = target;
            canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera;
            canvas.planeDistance = camera.nearClipPlane * 2;
            foreach (var childCanvas in childCanvases) { childCanvas.renderMode = RenderMode.ScreenSpaceCamera; childCanvas.worldCamera = camera; childCanvas.planeDistance = canvas.planeDistance; }
            Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active = target;
            image.ReadPixels(new Rect(0, 0, 1280, 720), 0, 0); image.Apply();
            File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG()); screenshots++;
        }
        finally
        {
            canvas.renderMode = previousMode; canvas.worldCamera = previousCamera;
            for (int i = 0; i < childCanvases.Length; i++) { childCanvases[i].renderMode = childModes[i]; childCanvases[i].worldCamera = childCameras[i]; }
            camera.targetTexture = previousTarget; RenderTexture.active = previousActive;
            target.Release(); Destroy(target); Destroy(image);
        }
    }
    void OnDestroy() => Application.logMessageReceived -= LogError;
}
#endif
