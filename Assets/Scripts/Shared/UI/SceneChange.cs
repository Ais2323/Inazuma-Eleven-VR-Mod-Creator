using UnityEngine;
using UnityEngine.SceneManagement;
using System.Collections;
using System.Collections.Generic;

public class SceneChange : MonoBehaviour
{
    public void OnClickToScene(string sceneName)
    {
        ScenePageCache.Open(sceneName);
    }
}

// Keep the editors (including unsaved models) alive while another page is shown.
// The host must live outside those scenes so hiding a page cannot stop navigation.
internal sealed class ScenePageCache : MonoBehaviour
{
    static ScenePageCache instance;
    readonly Dictionary<int, List<GameObject>> visibleRoots = new();
    bool changing;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
    static void ResetInstance() => instance = null;

    public static void Open(string sceneName)
    {
        if (string.IsNullOrWhiteSpace(sceneName)) return;
        if (instance == null)
        {
            var host = new GameObject("Scene Page Cache");
            DontDestroyOnLoad(host);
            instance = host.AddComponent<ScenePageCache>();
        }
        if (!instance.changing) instance.StartCoroutine(instance.Change(sceneName));
    }

    IEnumerator Change(string sceneName)
    {
        Scene source = SceneManager.GetActiveScene();
        Scene target = SceneManager.GetSceneByName(sceneName);
        if (target == source) yield break;
        if (!target.isLoaded && !Application.CanStreamedLevelBeLoaded(sceneName))
        {
            Debug.LogError("[SceneChange] Scene is not in the build: " + sceneName);
            yield break;
        }
        changing = true;
        try
        {
            // Deactivating a GameObject stops its coroutines permanently. Let the
            // initial list/model construction finish before parking the editor.
            foreach (GameObject root in source.GetRootGameObjects())
                foreach (var editor in root.GetComponentsInChildren<UniformMakerController>(true))
                    while (editor != null && editor.isActiveAndEnabled && editor.IsInitializing) yield return null;

            var roots = new List<GameObject>();
            foreach (GameObject root in source.GetRootGameObjects())
                if (root.activeSelf) roots.Add(root);
            visibleRoots[source.handle] = roots;
            foreach (GameObject root in roots) if (root != null) root.SetActive(false);

            if (!target.isLoaded)
            {
                yield return SceneManager.LoadSceneAsync(sceneName, LoadSceneMode.Additive);
                target = SceneManager.GetSceneByName(sceneName);
            }
            if (!target.IsValid() || !target.isLoaded)
            {
                foreach (GameObject root in roots) if (root != null) root.SetActive(true);
                yield break;
            }
            SceneManager.SetActiveScene(target);
            if (visibleRoots.TryGetValue(target.handle, out var restored))
                foreach (GameObject root in restored) if (root != null) root.SetActive(true);
        }
        finally { changing = false; }
    }
}
