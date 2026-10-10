#if UNITY_EDITOR || DEVELOPMENT_BUILD
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;
using G4Composition;

public sealed class G4CompositionSmokeTest : MonoBehaviour
{
    string output;
    static readonly BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
    static void Initialize()
    {
        string[] args = Environment.GetCommandLineArgs(); int flag = Array.IndexOf(args, "--g4-composition-smoke");
        if (flag < 0 || flag + 2 >= args.Length) return;
        string output = Path.GetFullPath(args[flag + 1]); Directory.CreateDirectory(output);
        SettingManager.SettingPath = Path.Combine(output, "test-settings.json"); SettingManager.UpdateInfo("path", args[flag + 2]);
        var runner = new GameObject("Composition smoke").AddComponent<G4CompositionSmokeTest>(); runner.output = output; DontDestroyOnLoad(runner);
    }
    static T Field<T>(object instance, string name) => (T)instance.GetType().GetField(name, Flags).GetValue(instance);
    static void Call(object instance, string name, params object[] args) => instance.GetType().GetMethod(name, Flags).Invoke(instance, args);
    static string ActivePath(CompositionEditor editor)
    {
        var active = Field<object>(editor, "active"); if (active == null) return null;
        return ((RawHeadAsset)active.GetType().GetField("Raw").GetValue(active)).RelativePath;
    }
    async Task Until(Func<bool> predicate)
    {
        float timeout = Time.realtimeSinceStartup + 120;
        while (!predicate()) { if (Time.realtimeSinceStartup > timeout) throw new TimeoutException("Composition smoke timed out."); await Task.Delay(100); }
        await Task.Delay(250);
    }
    async void Start()
    {
        try
        {
            CharacterPreviewDemo demo = null;
            await Until(() => { demo = FindFirstObjectByType<CharacterPreviewDemo>(); return demo != null && demo.CurrentHead != null; });
            demo.SetCompositionMode(true);
            var editor = FindFirstObjectByType<CompositionEditor>();
            var source = Field<Dropdown>(editor, "sourceDropdown");
            int first = source.options.FindIndex(o => o.text.EndsWith("c01000010/c01000010", StringComparison.Ordinal));
            int second = source.options.FindIndex(o => o.text.EndsWith("c11010010/c11010010", StringComparison.Ordinal));
            if (first < 0 || second < 0) throw new Exception("Smoke sample missing.");
            source.value = first; await Until(() => ActivePath(editor)?.EndsWith("c01000010.g4md", StringComparison.Ordinal) == true);
            source.value = second; source.value = first; source.value = second;
            await Until(() => ActivePath(editor)?.EndsWith("c11010010.g4md", StringComparison.Ordinal) == true);
            Call(editor, "SelectAll", true); Call(editor, "AddSelected");
            int count = editor.Document.Parts.Count; if (count != 3) throw new Exception("Expected three source meshes.");
            var fields = Field<List<InputField>>(editor, "fields"); fields[0].text = "0.025"; fields[0].onEndEdit.Invoke("0.025"); fields[6].text = "1.1"; fields[6].onEndEdit.Invoke("1.1");
            if (Math.Abs(editor.Document.Parts.Last().Position.x - .025f) > .00001f || Math.Abs(editor.Document.Parts.Last().Scale.x - 1.1f) > .00001f) throw new Exception("XYZ fields failed.");
            fields[6].text = "-1"; fields[6].onEndEdit.Invoke("-1");
            if (editor.Document.Parts.Last().Scale.x <= 0) throw new Exception("Negative scale accepted.");
            fields[6].text = "1.1"; fields[6].onEndEdit.Invoke("1.1"); Call(editor, "Fit");
            await Task.Delay(300); Capture(editor, "composition");
            var target = Field<Dropdown>(editor, "targetDropdown"); target.value = first;
            await Until(() => Field<RawHeadAsset>(editor, "target")?.RelativePath.EndsWith("c01000010.g4md", StringComparison.Ordinal) == true);
            string exported = await new CompositionExporter(DumpDataManager.CommonPath, DumpDataManager.Dx11Path).ExportAsync(new ExportRequest { Document = editor.Document, TargetRelativePath = Field<RawHeadAsset>(editor, "target").RelativePath, DestinationFolder = output }, CancellationToken.None);
            if (Directory.EnumerateFiles(exported, "*.g4sk", SearchOption.AllDirectories).Any()) throw new Exception("Replacement unexpectedly exported G4SK.");
            while (editor.Document.Parts.Count > 0) Call(editor, "RemoveSelected");
            await Task.Delay(300);
            if (Field<System.Collections.IList>(editor, "instances").Count != 0) throw new Exception("Removal failed.");
            int third = source.options.FindIndex(o => o.text.EndsWith("c02020810/c02020810", StringComparison.Ordinal));
            source.value = third;
            await Until(() => ActivePath(editor)?.EndsWith("c02020810.g4md", StringComparison.Ordinal) == true);
            if (Field<System.Collections.IDictionary>(editor, "sources").Count != 2 || editor.GetComponentsInChildren<G4Runtime.RuntimeHeadResources>(true).Any(r => r.name == "c11010010")) throw new Exception("Unused source resources were retained.");
            demo.SetCompositionMode(false); editor.gameObject.SetActive(false);
            File.WriteAllText(Path.Combine(output, "smoke-result.txt"), "PASS: conservative native replacement, rapid switching, transforms, scale guard, export without G4SK, removal and unused source release.\n" + exported);
            Finish(0);
        }
        catch (Exception e) { Debug.LogException(e); File.WriteAllText(Path.Combine(output, "smoke-result.txt"), "FAIL: " + e); Finish(1); }
    }
    void Capture(CompositionEditor editor, string name)
    {
        var camera = Field<Camera>(editor, "camera"); var canvas = Field<Canvas>(editor, "canvas"); var oldMode = canvas.renderMode; var oldCamera = canvas.worldCamera;
        var previous = RenderTexture.active; var previousTarget = camera.targetTexture;
        var rt = new RenderTexture(1440, 900, 24); var image = new Texture2D(1440, 900, TextureFormat.RGBA32, false);
        try
        {
            camera.targetTexture = rt; canvas.renderMode = RenderMode.ScreenSpaceCamera; canvas.worldCamera = camera; canvas.planeDistance = camera.nearClipPlane * 2;
            Canvas.ForceUpdateCanvases(); camera.Render(); RenderTexture.active = rt;
            image.ReadPixels(new Rect(0, 0, 1440, 900), 0, 0); image.Apply(); File.WriteAllBytes(Path.Combine(output, name + ".png"), image.EncodeToPNG());
        }
        finally { canvas.renderMode = oldMode; canvas.worldCamera = oldCamera; camera.targetTexture = previousTarget; RenderTexture.active = previous; rt.Release(); Destroy(rt); Destroy(image); }
    }
    void Finish(int code)
    {
#if UNITY_EDITOR
        UnityEditor.EditorApplication.isPlaying = false;
#else
        Application.Quit(code);
#endif
    }
}
#endif
