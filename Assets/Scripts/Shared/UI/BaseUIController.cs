using UnityEngine;

public abstract class BaseUIController : MonoBehaviour
{
    void Start()
    {
        LocalizationManager.AddComponment(this);
        UpdateLocalization();
    }
    public abstract void UpdateLocalization();
}
