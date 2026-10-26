using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using G4Runtime;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;
using UnityEngine.UI;

namespace G4Composition
{
    public sealed partial class CompositionEditor : MonoBehaviour
    {
        sealed class Source
        {
            public HeadEntry Entry; public RawHeadAsset Raw; public GameObject Root;
            public List<Piece> Pieces = new List<Piece>();
            public Dictionary<int, MeshFilter> Original = new Dictionary<int, MeshFilter>();
        }
        sealed class Piece
        {
            public Source Source; public int MeshIndex; public int[] Triangles; public string Name;
            public Mesh Mesh; public Material Material; public GameObject View; public bool Selected, Visible = true;
        }
        sealed class Instance { public CompositionPart Part; public GameObject View; }
        public CompositionDocument Document { get; private set; } = new CompositionDocument();
        readonly Dictionary<string, Source> sources = new Dictionary<string, Source>(StringComparer.OrdinalIgnoreCase);
        readonly List<Instance> instances = new List<Instance>();
        readonly List<InputField> fields = new List<InputField>();
        readonly Dictionary<uint, Dropdown> mappingFields = new Dictionary<uint, Dropdown>();
        IReadOnlyList<HeadEntry> heads;
        HeadModelLoader loader;
        CharacterPreviewDemo demo;
        Canvas canvas;
        new Camera camera;
        Transform orbit, content;
        Font font;
        Dropdown sourceDropdown, targetDropdown;
        Text status, rigInfo;
        RectTransform tree, assembly, mapping;
        InputField nameField;
        Source active;
        RawHeadAsset target;
        Instance selected;
        CancellationTokenSource lifetime, loading, exporting;
        int requestId, targetRequestId;
        string requestedSource, requestedTarget;
        bool dragging, writingFields, importing;
        float yaw, pitch, radius = 1, distance = 3;
        public void Initialize(CharacterPreviewDemo owner, IReadOnlyList<HeadEntry> entries, Font uiFont, Dropdown template)
        {
            demo = owner; heads = entries; font = uiFont; loader = new HeadModelLoader(); lifetime = new CancellationTokenSource();
            orbit = new GameObject("Composition orbit").transform; orbit.SetParent(transform, false);
            content = new GameObject("Authored coordinates").transform; content.SetParent(orbit, false);
            camera = new GameObject("Composition camera", typeof(Camera)).GetComponent<Camera>(); camera.transform.SetParent(transform, false);
            camera.fieldOfView = 35; camera.backgroundColor = new Color(.105f, .12f, .16f); camera.clearFlags = CameraClearFlags.SolidColor; camera.depth = 10;
            var co = new GameObject("Composition UI", typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster)); co.transform.SetParent(transform, false);
            canvas = co.GetComponent<Canvas>(); canvas.renderMode = RenderMode.ScreenSpaceOverlay; canvas.sortingOrder = 10;
            var scaler = co.GetComponent<CanvasScaler>(); scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize; scaler.referenceResolution = new Vector2(1440, 900);
            var left = Panel("Source panel", canvas.transform, new Vector2(0, 0), new Vector2(0, 1), new Vector2(8, 100), new Vector2(340, -8));
            var right = Panel("Composition panel", canvas.transform, new Vector2(1, 0), new Vector2(1, 1), new Vector2(-360, 100), new Vector2(-8, -8));
            Label(left, "來源角色與部件", 20, 10, 8, 310, 30);
            sourceDropdown = DropdownCopy(template, left, 10, 44, 312, 38);
            sourceDropdown.onValueChanged.AddListener(LoadSource);
            Button(left, "全選", 10, 88, 68, 30, () => SelectAll(true));
            Button(left, "取消", 82, 88, 68, 30, () => SelectAll(false));
            Button(left, "加入拼貼", 156, 88, 166, 30, AddSelected);
            tree = Scroll(left, 10, 126, 312, 565);
            Button(left, "顯示／隱藏來源參考", 10, 710, 312, 32, () => { if (active != null) active.Root.SetActive(!active.Root.activeSelf); });
            Label(left, "顯示只影響預覽；清單內所有部件都會匯出。", 12, 10, 751, 310, 35);
            Label(right, "拼貼清單", 20, 10, 8, 320, 30);
            assembly = Scroll(right, 10, 44, 330, 158);
            nameField = Input(right, "", 10, 210, 220, 30, value => { if (!writingFields && selected != null) { selected.Part.Name = value; RebuildAssembly(); } });
            Button(right, "移除", 240, 210, 100, 30, RemoveSelected);
            string[] axes = { "X", "Y", "Z" }, labels = { "位置", "旋轉", "縮放" };
            for (int row = 0; row < 3; row++)
            {
                Label(right, labels[row], 15, 10, 250 + row * 39, 44, 30);
                for (int axis = 0; axis < 3; axis++)
                {
                    Label(right, axes[axis], 13, 55 + axis * 96, 250 + row * 39, 15, 30);
                    fields.Add(Input(right, row == 2 ? "1" : "0", 70 + axis * 96, 250 + row * 39, 74, 30, _ => ApplyFields()));
                }
            }
            Button(right, "重設部件", 10, 372, 155, 30, () => { if (selected == null) return; selected.Part.Position = selected.Part.Rotation = Vector3.zero; selected.Part.Scale = Vector3.one; ShowFields(); ApplyTransform(selected); Fit(); });
            Button(right, "重設視角", 175, 372, 165, 30, Fit);
            Label(right, "替換目標角色", 17, 10, 412, 320, 28);
            targetDropdown = DropdownCopy(template, right, 10, 444, 330, 34);
            targetDropdown.onValueChanged.AddListener(LoadTarget);
            rigInfo = Label(right, "完整保留目標 G4MD 的 joint 索引；來源缺少的對應會合併至目標頭部 joint。不輸出 G4SK。", 12, 10, 485, 330, 65);
            mapping = Scroll(right, 10, 555, 330, 100);
            exportMode = Label(right, "模式：替換角色", 14, 10, 658, 330, 28);
            Button(right, "替換現有角色", 10, 690, 330, 30, () => { newCharacter = false; exportMode.text = "模式：替換現有角色"; });
            Button(right, "選擇資料夾並匯出", 10, 730, 330, 36, Export);
            Button(canvas.transform, "返回頭部預覽", 16, 806, 180, 34, () => { demo.SetCompositionMode(false); gameObject.SetActive(false); });
            Button(canvas.transform, "儲存拼貼文件", 205, 806, 180, 34, SaveDocument);
            Button(canvas.transform, "讀取拼貼文件", 395, 806, 180, 34, LoadDocument);
            status = Label(canvas.transform, "", 16, 16, 848, 1400, 45);
            sourceDropdown.interactable = targetDropdown.interactable = heads.Count != 0;
            if (heads.Count != 0) { LoadSource(0); LoadTarget(0); }
        }
        public void Resume() { gameObject.SetActive(true); Fit(); }
        async void LoadSource(int index)
        {
            if (index < 0 || index >= heads.Count) return;
            requestedSource = heads[index].RelativePath;
            loading?.Cancel(); loading?.Dispose(); loading = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token); int id = ++requestId;
            try
            {
                Status("讀取來源：" + heads[index].DisplayName);
                Source source = await GetSource(heads[index], loading.Token);
                if (id != requestId || this == null) return;
                if (active != null) active.Root.SetActive(false);
                active = source; active.Root.SetActive(true); RebuildTree(); Fit(); Status("勾選 mesh／連通塊後加入拼貼。左右拖曳旋轉，滾輪縮放。");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (id == requestId) Status(e.Message); Debug.LogException(e); }
            finally { ReleaseUnusedSources(); }
        }
        async Task<Source> GetSource(HeadEntry entry, CancellationToken token)
        {
            if (sources.TryGetValue(entry.RelativePath, out var cached)) return cached;
            var raw = await Task.Run(() =>
            {
                return RawHeadAsset.Read(DumpDataManager.CommonPath, DumpDataManager.Dx11Path, entry.RelativePath, token);
            }, token);
            var loaded = await loader.LoadHeadAsync(entry, token);
            if (token.IsCancellationRequested) { Destroy(loaded.Root); token.ThrowIfCancellationRequested(); }
            var source = new Source { Entry = entry, Raw = raw, Root = loaded.Root };
            source.Root.transform.SetParent(content, false); source.Root.SetActive(false);
            var owner = source.Root.GetComponent<RuntimeHeadResources>();
            var filters = source.Root.GetComponentsInChildren<MeshFilter>(true);
            for (int m = 0; m < filters.Length; m++)
            {
                var filter = filters[m]; var mesh = filter.sharedMesh;
                source.Original.Add(raw.Preview.Parts[m].SourceMeshIndex, filter);
                filter.gameObject.SetActive(false);
                var groups = ConnectedComponents.Split(mesh.vertexCount, mesh.triangles);
                for (int c = 0; c < groups.Count; c++)
                {
                    var piece = new Piece { Source = source, MeshIndex = raw.Preview.Parts[m].SourceMeshIndex, Triangles = groups[c], Name = mesh.name + " / " + (c + 1), Mesh = owner.Own(ConnectedComponents.Extract(mesh, groups[c])), Material = filter.GetComponent<MeshRenderer>().sharedMaterial };
                    piece.View = View(piece.Name, source.Root.transform, piece.Mesh, piece.Material);
                    source.Pieces.Add(piece);
                }
            }
            if (sources.TryGetValue(entry.RelativePath, out cached)) { Destroy(source.Root); return cached; }
            sources.Add(entry.RelativePath, source); return source;
        }
        async void LoadTarget(int index)
        {
            if (index < 0 || index >= heads.Count) return;
            requestedTarget = heads[index].RelativePath;
            int id = ++targetRequestId; target = null;
            try
            {
                var loaded = await GetSource(heads[index], lifetime.Token);
                if (id != targetRequestId || this == null) return;
                target = loaded.Raw; rigInfo.text = "完整保留目標 G4MD 的 joint 索引；來源缺少的對應會合併至目標頭部 joint。不輸出 G4SK。";
                RebuildMappings();
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { if (id == targetRequestId) rigInfo.text = e.Message; }
            finally { ReleaseUnusedSources(); }
        }
        void SelectAll(bool on) { if (active == null) return; foreach (var p in active.Pieces) { p.Selected = on; Highlight(p.View, on); } RebuildTree(); }
        void RebuildTree()
        {
            Clear(tree); if (active == null) return; float y = 0;
            Label(tree, active.Entry.DisplayName, 13, 0, y, 285, 36); y += 38;
            Label(tree, "來源權重會對應至目標 G4MD 的既有 joint 索引。", 11, 0, y, 285, 34); y += 36;
            foreach (var mesh in active.Pieces.GroupBy(p => p.MeshIndex))
            {
                var group = mesh.ToArray();
                Toggle(tree, active.Raw.Preview.Parts.First(m => m.SourceMeshIndex == mesh.Key).Name, group.All(p => p.Selected), 0, y, 280, value => { foreach (var p in group) { p.Selected = value; Highlight(p.View, value); } RebuildTree(); }); y += 30;
                foreach (var piece in group)
                {
                    Toggle(tree, (Array.IndexOf(group, piece) + 1) + " · " + piece.Triangles.Length + " faces", piece.Selected, 14, y, 207, value => { piece.Selected = value; Highlight(piece.View, value); });
                    Toggle(tree, "顯示", piece.Visible, 218, y, 70, value => { piece.Visible = value; piece.View.SetActive(value); });
                    y += 28;
                }
            }
            tree.sizeDelta = new Vector2(0, y);
        }
        void AddSelected()
        {
            if (active == null) return;
            foreach (var mesh in active.Pieces.Where(p => p.Selected).GroupBy(p => p.MeshIndex))
            {
                var first = mesh.First(); var triangles = mesh.SelectMany(p => p.Triangles).Distinct().OrderBy(t => t).ToArray();
                var part = new CompositionPart { Name = active.Raw.Preview.Parts.First(p => p.SourceMeshIndex == mesh.Key).Name, SourceRelativePath = active.Entry.RelativePath, MeshIndex = mesh.Key, Triangles = triangles };
                AddInstance(part, active);
            }
            active.Root.SetActive(false);
            RebuildAssembly(); ShowFields(); RebuildMappings(); Fit();
            ReleaseUnusedSources();
        }
        void ReleaseUnusedSources()
        {
            if (importing) return;
            var keep = new HashSet<string>(Document.Parts.Select(p => p.SourceRelativePath), StringComparer.OrdinalIgnoreCase);
            if (active != null) keep.Add(active.Entry.RelativePath);
            if (requestedSource != null) keep.Add(requestedSource);
            if (requestedTarget != null) keep.Add(requestedTarget);
            foreach (var pair in sources.Where(pair => !keep.Contains(pair.Key)).ToArray())
            {
                sources.Remove(pair.Key);
                if (pair.Value.Root != null) { pair.Value.Root.SetActive(false); Destroy(pair.Value.Root); }
            }
        }
        void AddInstance(CompositionPart part, Source source)
        {
            var original = source.Original[part.MeshIndex];
            var mesh = ConnectedComponents.Extract(original.sharedMesh, part.Triangles);
            var instance = new Instance { Part = part, View = View(part.Name, content, mesh, original.GetComponent<MeshRenderer>().sharedMaterial) };
            instance.View.AddComponent<RuntimeHeadResources>().Own(mesh);
            Document.Parts.Add(part); instances.Add(instance); selected = instance; ApplyTransform(instance);
        }
        void RebuildAssembly()
        {
            Clear(assembly); float y = 0;
            foreach (var instance in instances)
            {
                Button(assembly, (selected == instance ? "▶ " : "") + instance.Part.Name, 0, y, 246, 30, () => { selected = instance; RebuildAssembly(); ShowFields(); RebuildMappings(); });
                Toggle(assembly, "顯示", instance.Part.Visible, 252, y, 65, value => { instance.Part.Visible = value; instance.View.SetActive(value); });
                Highlight(instance.View, selected == instance); y += 34;
            }
            assembly.sizeDelta = new Vector2(0, y);
        }
        void RemoveSelected()
        {
            if (selected == null) return; Document.Parts.Remove(selected.Part); instances.Remove(selected); Destroy(selected.View); selected = instances.LastOrDefault();
            RebuildAssembly(); ShowFields(); RebuildMappings(); Fit();
            ReleaseUnusedSources();
        }
        void ShowFields()
        {
            writingFields = true;
            nameField.text = selected?.Part.Name ?? "";
            var vectors = selected == null ? new[] { Vector3.zero, Vector3.zero, Vector3.one } : new[] { selected.Part.Position, selected.Part.Rotation, selected.Part.Scale };
            for (int i = 0; i < fields.Count; i++) { fields[i].text = vectors[i / 3][i % 3].ToString("G7", CultureInfo.InvariantCulture); fields[i].interactable = selected != null; }
            writingFields = false;
        }
        void ApplyFields()
        {
            if (writingFields || selected == null) return;
            var numbers = new float[9];
            for (int i = 0; i < 9; i++) if (!float.TryParse(fields[i].text, NumberStyles.Float, CultureInfo.InvariantCulture, out numbers[i]) || float.IsNaN(numbers[i]) || float.IsInfinity(numbers[i]) || i >= 6 && numbers[i] <= 0) { Status("請輸入有效數值；XYZ 縮放必須大於零。"); return; }
            selected.Part.Position = new Vector3(numbers[0], numbers[1], numbers[2]); selected.Part.Rotation = new Vector3(numbers[3], numbers[4], numbers[5]); selected.Part.Scale = new Vector3(numbers[6], numbers[7], numbers[8]); ApplyTransform(selected); Status("");
        }
        static void ApplyTransform(Instance instance)
        {
            instance.View.transform.localPosition = instance.Part.Position; instance.View.transform.localRotation = Quaternion.Euler(instance.Part.Rotation); instance.View.transform.localScale = instance.Part.Scale; instance.View.SetActive(instance.Part.Visible);
        }
        void RebuildMappings()
        {
            Clear(mapping); mappingFields.Clear();
            if (selected == null) return;
            var source = sources[selected.Part.SourceRelativePath].Raw;
            var hashes = source.Meshes[selected.Part.MeshIndex].Palette.Select(h => h.ToString("X8"));
            Label(mapping, "保留 joint hash：\n" + string.Join(", ", hashes), 12, 0, 0, 310, 90);
        }
        async void Export()
        {
            if (exporting != null) { exporting.Cancel(); Status("正在取消匯出…"); return; }
            if (target == null || Document.Parts.Count == 0) { Status("請先加入部件並選擇目標角色。"); return; }
            string[] folders = SFB.StandaloneFileBrowser.OpenFolderPanel("選擇匯出位置（來源 Dump 不可寫入）", SettingManager.GetExportDirectory("compositionExportDirectory"), false);
            if (folders == null || folders.Length == 0 || string.IsNullOrEmpty(folders[0])) return;
            SettingManager.RememberExportDirectory("compositionExportDirectory", folders[0]);
            exporting = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            try
            {
                Status("驗證模型、材質與貼圖，並匯出中…再次按匯出可取消。");
                string path = await new CompositionExporter(DumpDataManager.CommonPath, DumpDataManager.Dx11Path).ExportAsync(new ExportRequest { Document = Document, DestinationFolder = folders[0], TargetRelativePath = target.RelativePath, Mode = ExportMode.ReplaceCharacter }, exporting.Token);
                Status("已匯出：" + path + "（尚未進行原遊戲驗證）");
            }
            catch (OperationCanceledException) { Status("已取消匯出。"); }
            catch (Exception e) { Status("匯出中止：" + e.Message); Debug.LogException(e); }
            finally { exporting.Dispose(); exporting = null; }
        }
        void SaveDocument()
        {
            string[] folder = SFB.StandaloneFileBrowser.OpenFolderPanel("選擇拼貼文件輸出資料夾", SettingManager.GetExportDirectory("compositionDocumentExportDirectory"), false); if (folder == null || folder.Length == 0) return;
            SettingManager.RememberExportDirectory("compositionDocumentExportDirectory", folder[0]);
            try
            {
                ExportPathGuard.Validate(folder[0], DumpDataManager.CommonPath, DumpDataManager.Dx11Path);
                string path = System.IO.Path.Combine(folder[0], "composition-" + Guid.NewGuid().ToString("N") + ".json");
                using (var stream = new System.IO.StreamWriter(new System.IO.FileStream(path, System.IO.FileMode.CreateNew))) stream.Write(JsonUtility.ToJson(Document, true));
                Status("已儲存：" + path);
            }
            catch (Exception e) { Status(e.Message); }
        }
        async void LoadDocument()
        {
            if (importing) return;
            string[] paths = SFB.StandaloneFileBrowser.OpenFilePanel("讀取拼貼文件", "", "json", false); if (paths == null || paths.Length == 0) return;
            importing = true;
            try
            {
                var doc = JsonUtility.FromJson<CompositionDocument>(System.IO.File.ReadAllText(paths[0]));
                if (doc == null || doc.Version != 1 || doc.Parts == null) throw new System.IO.InvalidDataException("無效的拼貼文件版本。");
                foreach (var part in doc.Parts)
                {
                    _ = part.Matrix;
                    var entry = heads.FirstOrDefault(h => h.RelativePath == part.SourceRelativePath) ?? throw new System.IO.InvalidDataException("缺少來源：" + part.SourceRelativePath);
                    var source = await GetSource(entry, lifetime.Token);
                    var mesh = source.Raw.Preview.Parts.FirstOrDefault(p => p.SourceMeshIndex == part.MeshIndex);
                    if (mesh == null || part.Triangles == null || part.Triangles.Length == 0 || part.Triangles.Any(t => t < 0 || t >= mesh.Indices.Length / 3)) throw new System.IO.InvalidDataException("無效的 mesh／三角形識別。原拼貼仍保留。");
                    if (part.Bones == null) part.Bones = new List<BoneMapping>();
                }
                foreach (var instance in instances) Destroy(instance.View); instances.Clear(); Document = new CompositionDocument { Id = doc.Id };
                foreach (var part in doc.Parts) AddInstance(part, sources[part.SourceRelativePath]);
                RebuildAssembly(); ShowFields(); RebuildMappings(); Fit(); Status("已讀取拼貼文件。");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { Status(e.Message); }
            finally { importing = false; ReleaseUnusedSources(); }
        }
        void Fit()
        {
            if (orbit == null) return; yaw = pitch = 0; orbit.rotation = Quaternion.identity; content.localPosition = Vector3.zero;
            var views = instances.Count > 0 ? instances.Select(i => i.View) : active == null ? Enumerable.Empty<GameObject>() : active.Pieces.Select(p => p.View);
            bool first = true; Bounds bounds = default;
            foreach (var view in views) { var r = view.GetComponent<Renderer>(); if (first) { bounds = r.bounds; first = false; } else bounds.Encapsulate(r.bounds); }
            if (first) return; content.localPosition = -bounds.center; radius = Mathf.Max(bounds.extents.magnitude, .01f); distance = radius / Mathf.Sin(camera.fieldOfView * Mathf.Deg2Rad * .5f) * 1.4f; ApplyView();
        }
        void Update()
        {
            if (camera == null || Mouse.current == null) return;
            bool ui = EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(); var mouse = Mouse.current;
            if (mouse.leftButton.wasPressedThisFrame) dragging = !ui;
            if (!mouse.leftButton.isPressed) dragging = false;
            if (dragging && !ui) { yaw += mouse.delta.ReadValue().x * .3f; pitch = Mathf.Clamp(pitch - mouse.delta.ReadValue().y * .3f, -85, 85); }
            if (!ui) distance = Mathf.Clamp(distance * Mathf.Exp(-mouse.scroll.ReadValue().y * .001f), radius * 1.15f, radius * 25);
            ApplyView();
        }
        void ApplyView() { orbit.rotation = Quaternion.Euler(pitch, yaw, 0); camera.transform.position = new Vector3(0, 0, -distance); camera.transform.rotation = Quaternion.identity; camera.nearClipPlane = Mathf.Max(radius * .005f, .0001f); camera.farClipPlane = Mathf.Max(distance + radius * 4, 10); }
        void Status(string message) { if (status != null) status.text = message; }
        static GameObject View(string name, Transform parent, Mesh mesh, Material material) { var go = new GameObject(name, typeof(MeshFilter), typeof(MeshRenderer)); go.transform.SetParent(parent, false); go.GetComponent<MeshFilter>().sharedMesh = mesh; go.GetComponent<MeshRenderer>().sharedMaterial = material; return go; }
        static void Highlight(GameObject view, bool selected) { var block = new MaterialPropertyBlock(); block.SetColor("_Color", selected ? new Color(1f, .78f, .4f) : Color.white); view.GetComponent<Renderer>().SetPropertyBlock(block); }
        static void Clear(Transform root) { foreach (Transform child in root) { child.gameObject.SetActive(false); Destroy(child.gameObject); } }
        static RectTransform Rect(string name, Transform parent, float x, float y, float w, float h) { var r = new GameObject(name, typeof(RectTransform)).GetComponent<RectTransform>(); r.SetParent(parent, false); r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h); return r; }
        static RectTransform Panel(string name, Transform parent, Vector2 min, Vector2 max, Vector2 low, Vector2 high) { var r = Rect(name, parent, 0, 0, 0, 0); r.anchorMin = min; r.anchorMax = max; r.offsetMin = low; r.offsetMax = high; r.gameObject.AddComponent<Image>().color = new Color(.05f, .07f, .1f, .97f); return r; }
        Text Label(Transform parent, string text, int size, float x, float y, float w, float h) { var r = Rect(text, parent, x, y, w, h); var t = r.gameObject.AddComponent<Text>(); t.font = font; t.fontSize = size; t.color = Color.white; t.text = text; t.raycastTarget = false; t.alignment = TextAnchor.MiddleLeft; return t; }
        void Button(Transform parent, string text, float x, float y, float w, float h, Action action) { var r = Rect(text, parent, x, y, w, h); var image = r.gameObject.AddComponent<Image>(); image.color = new Color(.2f, .3f, .4f); var b = r.gameObject.AddComponent<Button>(); b.targetGraphic = image; b.onClick.AddListener(() => action()); Label(r, text, 15, 5, 0, w - 10, h).alignment = TextAnchor.MiddleCenter; }
        InputField Input(Transform parent, string text, float x, float y, float w, float h, Action<string> action) { var r = Rect("Input", parent, x, y, w, h); var image = r.gameObject.AddComponent<Image>(); image.color = new Color(.15f, .2f, .27f); var t = Label(r, "", 14, 5, 0, w - 10, h); var field = r.gameObject.AddComponent<InputField>(); field.targetGraphic = image; field.textComponent = t; field.text = text; field.onEndEdit.AddListener(value => action(value)); return field; }
        void Toggle(Transform parent, string label, bool value, float x, float y, float width, Action<bool> action) { var r = Rect("Toggle", parent, x, y, width, 26); var t = r.gameObject.AddComponent<Toggle>(); var box = Rect("Box", r, 0, 4, 18, 18); var bg = box.gameObject.AddComponent<Image>(); bg.color = new Color(.25f, .3f, .4f); var mark = Label(box, "✓", 16, 0, -2, 20, 22); t.targetGraphic = bg; t.graphic = mark; Label(r, label, 12, 23, 0, width - 23, 26); t.isOn = value; t.onValueChanged.AddListener(v => action(v)); }
        Dropdown DropdownCopy(Dropdown template, Transform parent, float x, float y, float w, float h) { var go = Instantiate(template.gameObject, parent); go.name = "Dropdown"; var r = (RectTransform)go.transform; r.anchorMin = r.anchorMax = r.pivot = new Vector2(0, 1); r.anchoredPosition = new Vector2(x, -y); r.sizeDelta = new Vector2(w, h); var d = go.GetComponent<Dropdown>(); d.onValueChanged = new Dropdown.DropdownEvent(); d.interactable = true; d.captionText.fontSize = 13; d.itemText.fontSize = 13; return d; }
        static RectTransform Scroll(Transform parent, float x, float y, float w, float h) { var r = Rect("Scroll", parent, x, y, w, h); var bg = r.gameObject.AddComponent<Image>(); bg.color = new Color(.08f, .1f, .14f); r.gameObject.AddComponent<RectMask2D>(); var s = r.gameObject.AddComponent<ScrollRect>(); s.horizontal = false; s.movementType = ScrollRect.MovementType.Clamped; s.scrollSensitivity = 28; var c = Rect("Content", r, 0, 0, w, h); c.anchorMax = new Vector2(1, 1); c.sizeDelta = new Vector2(0, h); s.content = c; s.viewport = r; return c; }
        void OnDestroy() { loading?.Cancel(); exporting?.Cancel(); lifetime?.Cancel(); loading?.Dispose(); lifetime?.Dispose(); }
    }
}

