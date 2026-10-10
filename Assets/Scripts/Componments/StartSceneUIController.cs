using UnityEngine;
using UnityEngine.UI;
using SFB;
using System.IO;
using UnityEngine.SceneManagement;

public class StartSceneUIController : BaseUIController
{
    public InputField dirInputField;
    public Button LoadDumpBtn;
    public Text DirPlaceholderText;
    public Text BrowseText;
    public Text LoadDump;

    public string path = "";

    void Awake()
    {
        dirInputField.text = SettingManager.Settings["path"];
        path = dirInputField.text;
        LoadDumpBtn.interactable = !string.IsNullOrWhiteSpace(path);
        if (DumpDataManager.ValidateSavedPath()) SceneManager.LoadSceneAsync("CharacterPreviewDemo");
    }

    public override void UpdateLocalization()
    {
        DirPlaceholderText.text = LocalizationManager.GetText("dirPlaceholder");
        BrowseText.text = LocalizationManager.GetText("browse");
        LoadDump.text = LocalizationManager.GetText("loadDump");
    }

    public void OnChangePath(string _)
    {
        path = dirInputField.text;
        LoadDumpBtn.interactable = path != "";
    }

    public void OnClickBrowse()
    {
        // 異步或同步開啟
        string[] paths = StandaloneFileBrowser.OpenFolderPanel(LocalizationManager.GetText("dirPlaceholder"), path, false);

        if (paths.Length > 0) 
            dirInputField.text = paths[0];
    }

    public void OnClickLoadDump()
    {
        if (Directory.Exists(path) == false) return;
        // 更新load Dump路徑
        bool isSuccess = DumpDataManager.UpdateRootPath(path);
        dirInputField.text = SettingManager.Settings["path"];
        
        if (isSuccess == false)
        {
            Debug.LogWarning("Dump Path is invalid");
            return;
        }
        SceneManager.LoadSceneAsync("CharacterPreviewDemo");
    }
}
