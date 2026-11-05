using UnityEngine;

// Preserve existing CharacterMaker scene references and serialized camera fields.
public sealed class CharacterPreviewCameraController : PreviewCameraController
{
    public CharacterManager characterManager;
    protected override Vector3 Center => characterManager != null && characterManager.TryGetPreviewNeck(out var point)
        ? point : fallbackCenter;
}
