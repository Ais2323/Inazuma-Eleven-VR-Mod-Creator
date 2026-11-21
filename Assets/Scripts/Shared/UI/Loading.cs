using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.UI;

// One persistent overlay runs work independently of the scene/page that started it.
[DisallowMultipleComponent]
public sealed class Loading : MonoBehaviour
{
    [SerializeField] Canvas overlay;
    [SerializeField] GraphicRaycaster raycaster;
    [SerializeField] Text infoText;
    [SerializeField] Text cancelText;
    [SerializeField] Image progressBar;
    [SerializeField] Button cancelButton;
    [SerializeField] GameObject loadingPanel;
    [SerializeField] GameObject resultPanel;
    [SerializeField] Text resultText;
    [SerializeField] Text resultCloseText;
    [SerializeField] Button resultCloseButton;
    readonly List<string> results = new();
    static Loading instance;
    Session current;

    sealed class Session
    {
        internal readonly Stack<IEnumerator> Steps = new();
        internal Func<IEnumerator> Work;
        internal Action Cancel;
        internal Coroutine Routine;
        internal bool Finished, Cancelled;
        internal int Dependents;
        internal Session Parent;
        internal readonly List<Session> Children = new();
    }

    public static bool IsRunning => instance != null && instance.current != null;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetInstance() => instance = null;

    void Awake()
    {
        if (instance != null && instance != this) { gameObject.SetActive(false); Destroy(gameObject); return; }
        instance = this;
        transform.SetParent(null, false);
        DontDestroyOnLoad(gameObject);
        cancelButton.onClick.AddListener(Cancel);
        resultCloseButton.onClick.AddListener(DismissInfo);
        infoText.supportRichText = false;
        SetVisible(false);
    }

    internal static void EnsureAvailable() => GetInstance();

    static Loading GetInstance()
    {
        if (instance != null) return instance;
        // Reuse a scene-authored instance, including one that starts inactive.
        var existing = FindFirstObjectByType<Loading>(FindObjectsInactive.Include);
        if (existing != null) {
            existing.transform.SetParent(null, false);
            existing.gameObject.SetActive(true);
            return instance;
        }
        var prefab = Resources.Load<Loading>("Loading");
        if (prefab == null) throw new InvalidOperationException("Missing Resources/Loading prefab.");
        return Instantiate(prefab);
    }

    // Returns false while another job is running; never overwrites its cancellation handler.
    public static bool Run(Func<IEnumerator> work, Action onCancel = null)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        var host = GetInstance();
        if (host.current != null) return false;
        var session = new Session { Work = work, Cancel = onCancel };
        host.current = session;
        host.progressBar.fillAmount = 0;
        host.infoText.text = LocalizationManager.GetText("Loading", "Common");
        host.cancelText.text = LocalizationManager.GetText("Cancel", "Common");
        host.cancelButton.interactable = onCancel != null;
        host.SetVisible(true);
        session.Routine = host.StartCoroutine(host.Execute(session));
        return true;
    }

    public static bool Run(Action work, Action onCancel = null)
    {
        if (work == null) throw new ArgumentNullException(nameof(work));
        return Run(() => Invoke(work), onCancel);
    }

    static IEnumerator Invoke(Action work) { work(); yield break; }

    // Nested editor operations share the current overlay instead of replacing it.
    public static void Perform(Action work, string textKey)
    {
        if (!Application.isPlaying || IsRunning) { work(); return; }
        Run(() => Stages(work, textKey));
    }

    static IEnumerator Stages(Action work, string key)
    {
        UpdateText(LocalizationManager.GetText(key));
        UpdateProgress(5);
        yield return null;
        work();
        UpdateProgress(100);
        yield return null;
    }

    // Also used by scene Start coroutines: the parent overlay remains visible
    // until separately scheduled initialization/thumbnail coroutines finish.
    public static IEnumerator Track(IEnumerator work, string textKey)
    {
        var host = GetInstance();
        Session session;
        if (host.current != null) {
            var parent = host.current;
            session = new Session { Work = () => work, Parent = parent };
            parent.Dependents++;
            parent.Children.Add(session);
            session.Routine = host.StartCoroutine(host.Execute(session));
        }
        else {
            Run(() => WithTitle(work, textKey));
            session = host.current;
        }
        // The persistent host owns the work. Hiding or destroying the initiating
        // scene only stops this waiter, never strands the overlay/dependent count.
        while (!session.Finished) yield return null;
    }

    static IEnumerator WithTitle(IEnumerator work, string key)
    {
        UpdateText(LocalizationManager.GetText(key));
        yield return work;
        UpdateProgress(100);
    }

    public static bool RunTask(Func<Task> work, string textKey, Action onCancel = null)
        => Run(() => WaitTask(work, textKey), onCancel);

    static IEnumerator WaitTask(Func<Task> work, string key)
    {
        UpdateText(LocalizationManager.GetText(key));
        Task task = work();
        // Observe faults even when cancellation stops this coroutine before the
        // asynchronous operation acknowledges its cancellation token.
        _ = task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        while (!task.IsCompleted) yield return null;
        if (task.IsFaulted) throw task.Exception.GetBaseException();
        if (!task.IsCanceled) UpdateProgress(100);
    }

    public static Task TrackTask(Func<Task> work, string textKey)
    {
        if (IsRunning) return work();
        var completion = new TaskCompletionSource<bool>();
        RunTask(async () => {
            try { await work(); completion.TrySetResult(true); }
            catch (Exception error) { completion.TrySetException(error); }
        }, textKey);
        return completion.Task;
    }

    public static void UpdateProgress(int progress)
    {
        if (IsRunning) instance.progressBar.fillAmount = Mathf.Clamp(progress, 0, 100) / 100f;
    }

    public static void UpdateText(string text)
    {
        if (IsRunning) instance.infoText.text = text ?? "";
    }

    public static void Cancel()
    {
        if (!IsRunning || instance.current.Cancel == null) return;
        var host = instance;
        var session = host.current;
        // Stop native yield waits as well (e.g. WaitForSeconds), then dispose all
        // nested enumerators so the caller's finally blocks can release resources.
        session.Cancelled = true;
        if (session.Routine != null) host.StopCoroutine(session.Routine);
        host.Finish(session, true);
    }

    IEnumerator Execute(Session session)
    {
        // Let the overlay render before invoking user code on the main thread.
        yield return null;
        try {
            IEnumerator first = null;
            try { first = session.Work(); }
            catch (Exception error) { AppLog.Exception(error, this); }
            if (first != null) session.Steps.Push(first);
            while (!session.Finished && session.Steps.Count > 0) {
                var step = session.Steps.Peek();
                bool more = false;
                object yielded = null;
                try { more = step.MoveNext(); if (more) yielded = step.Current; }
                catch (Exception error) { AppLog.Exception(error, this); break; }
                if (session.Finished) yield break;
                if (!more) { session.Steps.Pop(); Dispose(step); continue; }
                if (yielded is IEnumerator nested) session.Steps.Push(nested);
                else yield return yielded;
            }
            while (!session.Finished && session.Dependents > 0) yield return null;
        }
        finally { Finish(session, session.Cancelled); }
    }

    void Finish(Session session, bool cancelled)
    {
        if (session.Finished) return;
        session.Finished = true;
        foreach (var child in session.Children.ToArray()) {
            if (child.Routine != null) StopCoroutine(child.Routine);
            Finish(child, cancelled);
        }
        if (session.Parent != null) {
            session.Parent.Dependents--;
            session.Parent.Children.Remove(session);
        }
        // Capture handler before disposing: cancellation runs once after cleanup.
        var handler = cancelled ? session.Cancel : null;
        while (session.Steps.Count > 0) Dispose(session.Steps.Pop());
        if (current == session) { current = null; SetVisible(false); }
        try { handler?.Invoke(); }
        catch (Exception error) { AppLog.Exception(error, this); }
    }

    static void Dispose(IEnumerator step)
    {
        try { (step as IDisposable)?.Dispose(); }
        catch (Exception error) { AppLog.Exception(error); }
    }

    void Update()
    {
        // The log callback can run on worker threads; no UI API is used there.
        bool changed = false;
        int count = 0;
        while (count++ < 256 && AppLog.TryRead(out var entry)) {
            if (IsRunning) infoText.text = entry.Text;
            if (entry.ShowInfo && !results.Contains(entry.Text)) {
                if (results.Count >= 12) results.RemoveAt(0);
                results.Add(entry.Text);
                changed = true;
            }
        }
        if (current == null && results.Count > 0 && (changed || !resultPanel.activeSelf)) ShowInfo();
    }

    public void DismissInfo()
    {
        results.Clear();
        resultText.text = "";
        resultPanel.SetActive(false);
        if (current == null) { overlay.enabled = false; raycaster.enabled = false; }
    }

    void ShowInfo()
    {
        loadingPanel.SetActive(false);
        resultPanel.SetActive(true);
        resultText.supportRichText = false;
        resultText.text = string.Join("\n\n", results);
        resultCloseText.text = LocalizationManager.GetText("Close", "Common");
        overlay.enabled = true;
        raycaster.enabled = true;
    }

    void SetVisible(bool visible)
    {
        loadingPanel.SetActive(visible);
        resultPanel.SetActive(false);
        overlay.enabled = visible;
        raycaster.enabled = visible;
        if (!visible && results.Count > 0) ShowInfo();
    }

    void OnDestroy()
    {
        if (instance != this) return;
        if (current != null) Finish(current, true);
        instance = null;
    }
}
