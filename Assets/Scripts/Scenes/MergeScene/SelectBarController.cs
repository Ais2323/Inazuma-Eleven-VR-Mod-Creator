using UnityEngine;
using UnityEngine.UI;

public class SelectBarController : MonoBehaviour
{
    public Text pathText;
    public string path;

    public void SetPath(string path)
    {
        pathText.text = path;
        this.path = path;
    }

    public void OnClickClose()
    {
        Destroy(gameObject);
    }
}
