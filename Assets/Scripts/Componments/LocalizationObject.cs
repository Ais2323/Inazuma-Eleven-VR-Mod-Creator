using UnityEngine.UI;

public class LocalizationObject : BaseUIController
{
    public string targetLoc;
    private Text target;

    public override void UpdateLocalization()
    {
        target.text = LocalizationManager.GetText(targetLoc);
    }

    void Awake()
    {
        target = GetComponent<Text>();
    }
}
