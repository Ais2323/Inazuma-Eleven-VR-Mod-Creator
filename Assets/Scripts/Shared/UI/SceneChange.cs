using UnityEngine;
using UnityEngine.SceneManagement;

public class SceneChange : MonoBehaviour
{
    public void OnClickToScene(string sceneName)
    {
        SceneManager.LoadSceneAsync(sceneName);
    }
}
