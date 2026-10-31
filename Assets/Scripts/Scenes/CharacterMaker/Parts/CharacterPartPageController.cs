using System.Globalization;
using UnityEngine;
using UnityEngine.UI;

// Shared controls for Head, Hair and Other; each concrete page has its own serialized UI references.
public abstract class CharacterPartPageController : MonoBehaviour
{
    public CharacterManager manager;
    public Transform transformInputParent;
    public RawImage textureImage, ocImage, spmImage;
    public abstract int Option { get; }
    readonly InputField[] inputs = new InputField[9];
    bool initialized;

    internal void Initialize(CharacterManager owner)
    {
        manager = owner;
        if (initialized) return;
        initialized = true;
        if (transformInputParent == null) return;
        for (int group = 0; group < 3; group++)
        {
            Transform row = transformInputParent.GetChild(group).GetChild(1);
            for (int axis = 0; axis < 3; axis++)
            {
                int index = group * 3 + axis;
                var input = row.GetChild(axis).GetComponent<InputField>();
                inputs[index] = input;
                input.onValueChanged.AddListener(value => OnTransformInputChanged(index, value));
                input.onEndEdit.AddListener(_ => NormalizeTransformInput(index));
            }
        }
        RefreshTransformInputs();
    }

    public void OnClickImportTexture() => manager.ImportTexture(Option);
    public void OnClickExportTextureAndUV() => manager.ExportTextureAndUV(Option);
    public void OnClickOpenSubPanel() => manager.selectionController.OnClickOpenSubPanel(Option);

    void OnTransformInputChanged(int index, string value)
    {
        if (!TryParse(value, out float number) || (index >= 6 && number <= 0)) return;
        manager.GetExtraTransform(Option, out Vector3 position, out Vector3 rotation, out Vector3 scale);
        int axis = index % 3;
        if (index < 3) position[axis] = number;
        else if (index < 6) rotation[axis] = number;
        else scale[axis] = number;
        manager.SetExtraTransform(Option, position, rotation, scale);
        manager.ApplyExtraTransform(Option);
    }

    void NormalizeTransformInput(int index)
    {
        if (inputs[index] == null) return;
        if (!TryParse(inputs[index].text, out float number) || (index >= 6 && number <= 0))
        {
            number = index >= 6 ? 1 : manager.GetExtraTransformValue(Option, index);
            if (index >= 6)
            {
                manager.GetExtraTransform(Option, out Vector3 position, out Vector3 rotation, out Vector3 scale);
                scale[index % 3] = 1;
                manager.SetExtraTransform(Option, position, rotation, scale);
                manager.ApplyExtraTransform(Option);
            }
        }
        inputs[index].SetTextWithoutNotify(Format(number));
    }

    internal void RefreshTransformInputs()
    {
        for (int i = 0; i < inputs.Length; i++)
            if (inputs[i] != null) inputs[i].SetTextWithoutNotify(Format(manager.GetExtraTransformValue(Option, i)));
    }

    static bool TryParse(string value, out float number) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out number) && float.IsFinite(number);
    static string Format(float number) => number.ToString("0.######", CultureInfo.InvariantCulture);
}
