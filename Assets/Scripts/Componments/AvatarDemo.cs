using System;
using System.Text;
using System.IO;
using G4Composition;
using G4Runtime;
using UnityEngine;

public class AvatarDemo : MonoBehaviour
{
    public string demoCode = "";
    public Transform headParent;
    [Range(0, 7)] public int previewExpression = 0;
    [Range(0, 7)] public int previewMouthExpression = 4;
    [Header("Parsed Result")]
    public AvatarRecipeData parsedData;
    [TextArea(4, 20)] public string parsedText;
    public string lastError;
    [Header("Image Import")]
    [TextArea(4, 8)] public string recognizedText;
    public string imageImportStatus;
    public bool IsReadingImage { get; private set; }
    public UnityEngine.Events.UnityEvent<string> onImageCodeRecognized = new UnityEngine.Events.UnityEvent<string>();
    private System.Threading.CancellationTokenSource imageReadCancellation;

    /// <summary>Bind this method to a UI Button's OnClick event.</summary>
    [ContextMenu("Import Recipe Image")]
    public void SelectRecipeImage()
    {
        if (IsReadingImage) return;
        string[] paths = SFB.StandaloneFileBrowser.OpenFilePanel("選擇角色編號圖片", "",
            new[] { new SFB.ExtensionFilter("圖片", "png", "jpg", "jpeg", "bmp") }, false);
        if (paths == null || paths.Length == 0) return;
        ReadRecipeImage(paths[0]);
    }

    public async void ReadRecipeImage(string path)
    {
        if (IsReadingImage) return;
        imageReadCancellation = new System.Threading.CancellationTokenSource();
        var cancellation = imageReadCancellation;
        bool codeRecognized = false;
        IsReadingImage = true;
        imageImportStatus = "正在辨識中央角色編號…";
        lastError = "";
        recognizedText = "";
        try
        {
            var result = await AvatarRecipeImageReader.ReadAsync(path, cancellation.Token);
            if (this == null || cancellation.IsCancellationRequested) return;
            recognizedText = string.Join("\n\n", result.candidates ?? new string[0]);
            if (!string.IsNullOrEmpty(result.error)) throw new InvalidDataException(result.error);
            string candidate = AvatarRecipeParser.ExtractImageCode(result.candidates);
            codeRecognized = true;
            demoCode = candidate;
            onImageCodeRecognized.Invoke(candidate);
            imageImportStatus = "角色編號已辨識，正在建立角色…";
            // Keep the recognized code even if the existing model decoder fails.
            var data = AvatarRecipeParser.Parse(candidate);
            demoCode = candidate;
            parsedData = data;
            parsedText = Format(data);
            CreateModel(data);
            imageImportStatus = "已從圖片讀取角色編號並建立角色。";
        }
        catch (OperationCanceledException) { }
        catch (Exception exception)
        {
            if (this != null)
            {
                lastError = exception.Message;
                imageImportStatus = (codeRecognized ? "角色編號已辨識，但轉換失敗：" : "辨識失敗：") + exception.Message;
                Debug.LogWarning(imageImportStatus, this);
            }
        }
        finally
        {
            cancellation.Dispose();
            if (this != null) { imageReadCancellation = null; IsReadingImage = false; }
        }
    }

    private void OnDestroy() => imageReadCancellation?.Cancel();

    private void Start()
    {
        if (!string.IsNullOrWhiteSpace(demoCode)) ParseDemoCode();
    }

    [ContextMenu("Parse Demo Code")]
    public void ParseDemoCode()
    {
        try
        {
            parsedData = AvatarRecipeParser.Parse(demoCode);
            lastError = "";
            parsedText = Format(parsedData);
            Debug.Log(parsedText, this);
            CreateModel(parsedData);
        }
        catch (Exception exception)
        {
            parsedData = null;
            parsedText = "";
            lastError = exception.Message;
            Debug.LogException(exception, this);
        }
    }

    private static string Format(AvatarRecipeData data)
    {
        var text = new StringBuilder();
        text.AppendLine("Avatar Recipe parsed result");
        text.Append("Fields: ").Append(data.Values.Count)
            .Append(", verification: ").AppendLine(data.VerificationCode);
        text.AppendLine("Index | Category | Param | Sub | Raw | Bits | Type | Num");
        foreach (AvatarRecipeValue value in data.Values)
        {
            text.Append(value.Index).Append(" | ")
                .Append(value.Category).Append(" | ")
                .Append(value.CategoryParam).Append(" | ")
                .Append(value.CategoryParamSub).Append(" | ")
                .Append(value.RawValue).Append(" | ")
                .Append(value.BitCount).Append(" | ")
                .Append(value.Type).Append(" | ")
                .AppendLine(value.Num.ToString());
        }
        return text.ToString();
    }

    [ContextMenu("Build Head")]
    private void CreateModel(AvatarRecipeData data)
    {
        if (data == null) return;
        // Resolve resources before replacing an existing avatar.
        AvatarHeadParts parts = AvatarRecipeParser.ResolveHeadParts(data);
        var faceBones = AvatarRecipeParser.ResolveFaceBoneTransforms(data);
        var earBones = AvatarRecipeParser.ResolveEarBoneTransforms(data, parts.Ear);
        foreach (var bone in faceBones) if (!earBones.ContainsKey(bone.Key)) earBones.Add(bone.Key, bone.Value);
        var attachment = AvatarRecipeParser.ResolveHeadAttachment(data);
        Transform parent = headParent != null ? headParent : transform;
        Transform previous = parent.Find("AvatarRecipeHead");
        if (previous != null)
        {
            if (Application.isPlaying) Destroy(previous.gameObject);
            else DestroyImmediate(previous.gameObject);
        }

        var rootObject = new GameObject("AvatarRecipeHead");
        rootObject.AddComponent<RuntimeHeadResources>();
        var root = rootObject.transform;
        root.SetParent(parent, false);


        // The h000xxxx resources are deformation targets. Render the face base;
        // the selected face type will be applied to it as vertex deformation.
        GameObject baseHead = Add(root, "_facebase", parts.HeadModel, parts.FaceTexture, "_facetex/00_face", true, faceBones);
        ApplyHeadAttachment(baseHead, attachment);
        Vector3 headOrigin = BoundsCenter(baseHead);
        ApplyColor(baseHead, RecipeColor(data, 0), true);

        GameObject hairBack = Add(root, "_hairB", parts.HairBack, parts.HairBack + "M", "_hairB", loadTextures: false);
        GameObject hairFront = Add(root, "_hairF", parts.HairFront, parts.HairFront + "M", "_hairF", loadTextures: false);
        GameObject hairUpper = Add(root, "_hairU", parts.HairUpper, parts.HairUpper + "M", "_hairU", loadTextures: false);
        ApplyHeadAttachment(hairBack, attachment);
        ApplyHeadAttachment(hairFront, attachment);
        ApplyHeadAttachment(hairUpper, attachment);
        GameObject ear = Add(root, "_ear", parts.Ear, "_facebase", "_facebase", true, earBones, parts.HeadModel);
        GameObject accessory = Add(root, "_accessory", parts.Accessory, "accessory_10", "_accessorytex", true);

        Texture2D facialParts = ComposeFacialParts(baseHead, data, parts, previewExpression, previewMouthExpression);
        ApplyFacialPartsTexture(baseHead, facialParts);

        // Face and hair vertices share head-local coordinates. Center the assembled
        // object once; translating hair by the face bounds would raise the hairline.
        root.localPosition = -headOrigin;

        Color skin = RecipeColor(data, 0);
        ApplyMaskedSkinColor(ear, skin);
        Color hairColor = RecipeColor(data, 1, category: 2);
        var hairDetails = LoadHairDetailMaps();
        ApplyHairBaseColor(hairBack, hairColor, hairDetails);
        ApplyHairBaseColor(hairFront, hairColor, hairDetails);
        ApplyHairBaseColor(hairUpper, hairColor, hairDetails);
        ApplyColor(accessory, hairColor, false);
        Debug.Log($"AvatarDemo head={parts.HeadModel}, face={parts.FaceTexture}, eye={parts.EyeTexture}, pupil={parts.PupilTexture}, highlight={parts.HighlightTexture}, eyebrow={parts.EyebrowTexture}, mouth={parts.MouthTexture}, hair={parts.HairFront}/{parts.HairBack}/{parts.HairUpper}, ear={parts.Ear}, accessory={parts.Accessory}", this);
    }

    private static void ApplyHeadAttachment(GameObject model, AvatarBoneTransform attachment)
    {
        if (model == null) return;
        model.transform.localPosition = attachment.Position;
        model.transform.localRotation = Quaternion.Euler(attachment.Rotation * Mathf.Rad2Deg);
        model.transform.localScale = attachment.Scale;
    }
    private static GameObject Add(Transform parent, string folder, string model, string texture, string textureFolder, bool fallbackTexture = false, System.Collections.Generic.IReadOnlyDictionary<uint, AvatarBoneTransform> boneTransforms = null, string skeletonModelHint = null, bool loadTextures = true)
    {
        if (string.IsNullOrEmpty(model)) return null;
        string modelPath = Path.Combine("chr", "_face", "20_EDIT", folder);
        string texturePath = Path.Combine("chr", "_face", "20_EDIT", textureFolder);
        try
        {
            GameObject part = G4ModelLoader.CreateModel(modelPath, model, texture, texturePath, fallbackTexture, true, boneTransforms, skeletonModelHint, loadTextures);
            part.transform.SetParent(parent, false);
            return part;
        }
        catch (Exception exception)
        {
            Transform orphan = parent.Find(model);
            if (orphan != null)
            {
                if (Application.isPlaying) Destroy(orphan.gameObject);
                else DestroyImmediate(orphan.gameObject);
            }
            Debug.LogWarning($"AvatarDemo failed to load {model}: {exception}");
            return null;
        }
    }

    private static Vector3 BoundsCenter(GameObject model)
    {
        if (model == null) return Vector3.zero;
        Renderer[] renderers = model.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return Vector3.zero;
        Bounds bounds = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
        return model.transform.parent.InverseTransformPoint(bounds.center);
    }


    private static Color RecipeColor(AvatarRecipeData data, int parameter, int firstSub = 0, int category = 5, bool lastMatch = false)
    {
        uint Channel(int sub, uint fallback)
        {
            uint value = data.Find(category, parameter, sub)?.RawValue ?? fallback;
            if (lastMatch) foreach (var field in data.Values)
                if (field.Category == category && field.CategoryParam == parameter && field.CategoryParamSub == sub)
                    value = field.RawValue;
            return value;
        }
        float h = Channel(firstSub, 0) / 63f;
        float s = Channel(firstSub + 1, 0) / 63f;
        float v = Channel(firstSub + 2, 63) / 63f;
        Color color = Color.HSVToRGB(Mathf.Repeat(h, 1f), Mathf.Clamp01(s), Mathf.Clamp01(v));
        color.a = 1f;
        return color;
    }

    private static void ApplyColor(GameObject model, Color color, bool wholeSkin)
    {
        if (model == null) return;
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
        foreach (Material material in renderer.materials)
        {
            if (material.HasProperty("_SkinColor")) material.SetColor("_SkinColor", color);
            if (material.HasProperty("_WholeSkin")) material.SetFloat("_WholeSkin", wholeSkin ? 1f : 0f);
            if (!wholeSkin && material.HasProperty("_Color")) material.SetColor("_Color", color);
        }
    }

    private static System.Collections.Generic.List<G4TextureParser.TextureData> LoadHairDetailMaps()
    {
        string path = Path.Combine(DumpDataManager.Dx11Path, "chr", "_face", "20_EDIT", "_hairtex", "hair_10.g4tx");
        return G4TextureParser.Parse(File.ReadAllBytes(path), path, new System.Collections.Generic.List<string>(), default, false);
    }

    private static Texture2D CropHairDetail(G4TextureParser.TextureData map, int column, int row, RuntimeHeadResources owner, string model, bool linear = true)
    {
        if (map == null) throw new InvalidDataException("Missing edit hair detail texture.");
        int blockBytes = map.Format == TextureFormat.DXT1 ? 8 :
            map.Format == TextureFormat.DXT5 || map.Format == TextureFormat.BC7 ? 16 : 0;
        if (blockBytes == 0) throw new InvalidDataException("Unsupported hair detail format: " + map.Format);
        int width = map.Width / 16, height = map.Height / 32;
        var pixels = new System.Collections.Generic.List<byte>();
        int sourceOffset = 0, sourceWidth = map.Width, sourceHeight = map.Height, mips = 0;
        // Keep only mips whose tile boundaries remain aligned to compression
        // blocks. Lower atlas mips mix adjacent hairstyles into the same block.
        for (int w = width, h = height; mips < map.MipCount && w >= 4 && h >= 4; w /= 2, h /= 2)
        {
            int stride = ((sourceWidth + 3) / 4) * blockBytes;
            int x = column * w, y = row * h;
            if ((x | y | w | h) % 4 != 0) break;
            int bytes = (w / 4) * blockBytes;
            for (int line = 0; line < h / 4; line++)
            {
                int offset = sourceOffset + (y / 4 + line) * stride + x / 4 * blockBytes;
                for (int i = 0; i < bytes; i++) pixels.Add(map.Pixels[offset + i]);
            }
            sourceOffset += stride * ((sourceHeight + 3) / 4);
            sourceWidth = Math.Max(1, sourceWidth / 2); sourceHeight = Math.Max(1, sourceHeight / 2);
            mips++;
        }
        if (mips == 0) throw new InvalidDataException("Hair tile does not align to texture compression blocks.");
        var texture = owner.Own(new Texture2D(width, height, map.Format, mips, linear)
        {
            name = model + " " + map.Name + " standalone",
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Trilinear,
            anisoLevel = 8
        });
        texture.LoadRawTextureData(pixels.ToArray());
        texture.Apply(false, true);
        return texture;
    }
    private static void ApplyHairBaseColor(GameObject model, Color hairColor, System.Collections.Generic.List<G4TextureParser.TextureData> details)
    {
        if (model == null) return;
        var resources = model.GetComponent<RuntimeHeadResources>();
        // Edit hair UVs address one tile of a 16-column, 32-row runtime atlas.
        // The loaded OC/SPM images contain just that tile. Preserve authored
        // margins by removing the tile origin, not normalizing mesh bounds.
        Vector2 minUV = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
        Vector2 maxUV = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
        foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>())
        foreach (Vector2 uv in filter.sharedMesh.uv)
        {
            minUV = Vector2.Min(minUV, uv);
            maxUV = Vector2.Max(maxUV, uv);
        }
        Vector2 tile = (minUV + maxUV) * .5f;
        int column = Mathf.FloorToInt(tile.x * 16f), row = Mathf.FloorToInt(tile.y * 32f);
        foreach (MeshFilter filter in model.GetComponentsInChildren<MeshFilter>())
        {
            Vector2[] uv = filter.sharedMesh.uv;
            for (int i = 0; i < uv.Length; i++) uv[i] = new Vector2(uv[i].x * 16f - column, uv[i].y * 32f - row);
            filter.sharedMesh.uv = uv;
        }
        Texture2D texture = CropHairDetail(details.Find(map => map.Name == "hair_10"), column, row, resources, model.name, false);
        Texture2D oc = CropHairDetail(details.Find(map => map.Name == "hair_10oc"), column, row, resources, model.name);
        Texture2D spm = CropHairDetail(details.Find(map => map.Name == "hair_10spm"), column, row, resources, model.name);
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
        foreach (Material material in renderer.sharedMaterials)
        {
            material.mainTexture = texture;
            material.SetColor("_HairColor", hairColor);
            material.SetFloat("_RecolorWhite", 1f);
            // Edit hair OC packs illumination into G; R is also bright in shadows.
            material.SetFloat("_OcUseGreen", 1f);
            material.SetVector("_DetailUvST", new Vector4(1f, 1f, 0f, 0f));
            material.SetTexture("_OcTex", oc);
            material.SetTexture("_SpmTex", spm);
            material.SetColor("_Color", Color.white);
            material.SetColor("_SkinColor", Color.white);
            material.SetTexture("_MskTex", null);
            material.SetFloat("_HasMsk", 0f);
            material.SetFloat("_WholeSkin", 0f);
        }
    }
    private static void ApplyMaskedSkinColor(GameObject model, Color skinColor)
    {
        if (model == null) return;
        foreach (Renderer renderer in model.GetComponentsInChildren<Renderer>())
        foreach (Material material in renderer.materials)
        {
            // Ear materials share face_10 and its mask/occlusion textures.
            // Recolor only the skin mask so the painted inner-ear shading is
            // retained. WholeSkin would flatten the complete ear to one color.
            if (material.HasProperty("_SkinColor")) material.SetColor("_SkinColor", skinColor);
            if (material.HasProperty("_WholeSkin")) material.SetFloat("_WholeSkin", 0f);
            if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
        }
    }
    private sealed class FaceLayer
    {
        public Texture2D Texture;
        public Texture2D Mask;
        public int Parameter;
        public bool Right;
        public string Resource;
    }

    private sealed class FacePose
    {
        public Vector2 Position;
        public float SharedHorizontalOffset;
        public Vector2 Scale = Vector2.one;
        public float Rotation;
    }

    private static void ApplyTextureAdjustment(GameObject head, FacePose pose, AvatarTextureTransform change, string meshName, bool sharedHorizontal = false)
    {
        // Highlight X is a common translation, not eye separation.
        pose.Position += new Vector2(sharedHorizontal ? 0f : change.Position.x, change.Position.y);
        if (sharedHorizontal) pose.SharedHorizontalOffset += change.Position.x;
        pose.Scale *= change.Scale;
        pose.Rotation += change.Rotation;
        if (Mathf.Abs(change.ModelHeight) < .000001f) return;
        // Convert model-space height to the mesh's actual UV span. The output
        // atlas has two rows and centered config coordinates span two units.
        Transform part = head.transform.Find(meshName);
        Mesh mesh = part == null ? null : part.GetComponent<MeshFilter>()?.sharedMesh;
        if (mesh == null) throw new InvalidOperationException("Missing facial mesh " + meshName);
        Vector3[] vertices = mesh.vertices;
        Vector2[] uv = mesh.uv;
        float minY = float.PositiveInfinity, maxY = float.NegativeInfinity;
        float minV = float.PositiveInfinity, maxV = float.NegativeInfinity;
        for (int i = 0; i < vertices.Length; i++)
        {
            minY = Mathf.Min(minY, vertices[i].y); maxY = Mathf.Max(maxY, vertices[i].y);
            minV = Mathf.Min(minV, uv[i].y); maxV = Mathf.Max(maxV, uv[i].y);
        }
        if (maxY - minY > .000001f)
            pose.Position.y += change.ModelHeight * (maxV - minV) / (maxY - minY) * 4f;
    }

    private static FacePose ReadFacePose(CfgTable table, string kind, string resource)
    {
        var pose = new FacePose();
        uint kindHash = G4TextureParser.Crc32(kind);
        uint resourceHash = string.IsNullOrEmpty(resource) ? 0u : G4TextureParser.Crc32(resource);
        uint baseScale = G4TextureParser.Crc32("TEX_PARTS_TYPE_BASE_SCALE_INFO");
        uint baseTrans = G4TextureParser.Crc32("TEX_PARTS_TYPE_BASE_TRANS_INFO");
        uint itemScale = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_SCALE_INFO");
        uint itemTrans = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_TRANS_INFO");
        foreach (CfgTable.Row row in table.Rows)
        {
            if (row.Values.Length < 3) continue;
            uint key = row.UInt(0);
            if (row.Hash == baseScale && key == kindHash || row.Hash == itemScale && key == resourceHash)
                pose.Scale = Vector2.Scale(pose.Scale, new Vector2(Convert.ToSingle(row.Values[1]), Convert.ToSingle(row.Values[2])));
            if (row.Hash == baseTrans && key == kindHash)
                pose.Position += new Vector2(Convert.ToSingle(row.Values[1]), Convert.ToSingle(row.Values[2]));
            // Item translations include a related-item count before X and Y.
            if (row.Hash == itemTrans && key == resourceHash && row.Values.Length >= 4)
                pose.Position += new Vector2(Convert.ToSingle(row.Values[2]), Convert.ToSingle(row.Values[3]));
        }
        return pose;
    }

    private static void ApplyEyeRelation(CfgTable table, string eye, string kind, FacePose pose)
    {
        uint itemTrans = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_TRANS_INFO");
        uint relate = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_TRANS_RELATE");
        uint relateScale = G4TextureParser.Crc32("TEX_PARTS_DEFAULT_TRANS_RELATE_SCALE");
        if (string.IsNullOrEmpty(eye)) return;
        uint eyeHash = G4TextureParser.Crc32(eye), kindHash = G4TextureParser.Crc32(kind);
        bool selectedEye = false, selectedKind = false;
        foreach (CfgTable.Row row in table.Rows)
        {
            if (row.Hash == itemTrans)
            {
                selectedEye = row.Values.Length > 0 && row.UInt(0) == eyeHash;
                selectedKind = false;
            }
            else if (selectedEye && row.Hash == relate && row.Values.Length >= 3)
            {
                selectedKind = row.UInt(0) == kindHash;
                if (selectedKind) pose.Position += new Vector2(Convert.ToSingle(row.Values[1]), Convert.ToSingle(row.Values[2]));
            }
            else if (selectedEye && selectedKind && row.Hash == relateScale && row.Values.Length >= 2)
                pose.Scale = Vector2.Scale(pose.Scale, new Vector2(Convert.ToSingle(row.Values[0]), Convert.ToSingle(row.Values[1])));
        }
    }

    private static FacePose ParentPose(FacePose parent, FacePose child)
    {
        Vector2 offset = Vector2.Scale(parent.Scale, child.Position);
        float c = Mathf.Cos(parent.Rotation), s = Mathf.Sin(parent.Rotation);
        child.Position = parent.Position + new Vector2(c * offset.x - s * offset.y, s * offset.x + c * offset.y);
        child.SharedHorizontalOffset = parent.SharedHorizontalOffset + child.SharedHorizontalOffset * parent.Scale.x;
        child.Scale = Vector2.Scale(parent.Scale, child.Scale);
        child.Rotation += parent.Rotation;
        return child;
    }

    private static Vector4 TextureRotation(FacePose pose, bool right)
    {
        float angle = right ? -pose.Rotation : pose.Rotation;
        float c = Mathf.Cos(angle), s = Mathf.Sin(angle);
        return new Vector4(c, -s, s, c);
    }

    private static Vector4 Placement(FacePose pose, bool right)
    {
        // Config translations use centered, upward-positive coordinates. DDS
        // rows and mesh UVs are native top-to-bottom, so invert the Y offset.
        return new Vector4(.5f + ((right ? -pose.Position.x : pose.Position.x) + pose.SharedHorizontalOffset) * .5f,
            .5f - pose.Position.y * .5f, pose.Scale.x, pose.Scale.y);
    }

    private static Texture2D ComposeFacialParts(GameObject owner, AvatarRecipeData data, AvatarHeadParts parts, int expression = 0, int mouthExpression = 4)
    {
        if (owner == null) return null;
        string posePath = Path.Combine(DumpDataManager.CommonPath, "chr", "_face", "20_EDIT", "texPartsDefaultPose.cfg.bin");
        CfgTable poseTable = CfgTable.Read(File.ReadAllBytes(posePath), posePath);
        var adjustments = AvatarRecipeParser.ResolveTextureTransforms(data);
        FacePose eyePose = ReadFacePose(poseTable, "eye", parts.EyeTexture);
        ApplyTextureAdjustment(owner, eyePose, adjustments[1], "parts_eye_10");
        FacePose pupilPose = ReadFacePose(poseTable, "pupil", parts.PupilTexture);
        ApplyEyeRelation(poseTable, parts.EyeTexture, "pupil", pupilPose);
        ApplyTextureAdjustment(owner, pupilPose, adjustments[2], "parts_eye_10");
        ParentPose(eyePose, pupilPose);
        FacePose highlightPose = ReadFacePose(poseTable, "highlight", parts.HighlightTexture);
        ApplyTextureAdjustment(owner, highlightPose, adjustments[3], "parts_eye_10", sharedHorizontal: true);
        ParentPose(pupilPose, highlightPose);
        FacePose eyebrowPose = ReadFacePose(poseTable, "eyebrow", parts.EyebrowTexture);
        ApplyEyeRelation(poseTable, parts.EyeTexture, "eyebrow", eyebrowPose);
        ApplyTextureAdjustment(owner, eyebrowPose, adjustments[4], "parts_eye_10");
        FacePose mouthPose = ReadFacePose(poseTable, "mouth", parts.MouthTexture);
        ApplyTextureAdjustment(owner, mouthPose, adjustments[5], "parts_mouth_10");
        var poses = new[] { eyePose, pupilPose, highlightPose, eyebrowPose, mouthPose };
        var layers = new System.Collections.Generic.List<FaceLayer>();
        // Feature 1 is the full-face foundation, below every expression layer.
        AddFaceLayers(layers, "00_face", parts.FaceTexture, 0);
        AddFaceLayers(layers, "01_eye", parts.EyeTexture, 1);
        AddFaceLayers(layers, "02_pupil", parts.PupilTexture, 2);
        AddFaceLayers(layers, "03_highlight", parts.HighlightTexture, 3);
        AddFaceLayers(layers, "04_eyebrow", parts.EyebrowTexture, 4);
        AddFaceLayers(layers, "05_mouth", parts.MouthTexture, 5);
        if (layers.Count == 0) return null;
        Shader shader = Resources.Load<Shader>("AvatarFaceComposite");
        if (shader == null || !shader.isSupported)
        {
            foreach (FaceLayer layer in layers) { Destroy(layer.Texture); if (layer.Mask != null) Destroy(layer.Mask); }
            throw new InvalidOperationException("AvatarFaceComposite shader is missing or unsupported.");
        }
        var material = new Material(shader);
        const int width = 2048, height = 1024;
        var a = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        var b = RenderTexture.GetTemporary(width, height, 0, RenderTextureFormat.ARGB32);
        RenderTexture initial = RenderTexture.active;
        RenderTexture.active = a;
        Color skin = RecipeColor(data, 0);
        Color empty = QualitySettings.activeColorSpace == ColorSpace.Linear ? skin.linear : skin;
        empty.a = 0f;
        GL.Clear(true, true, empty);
        RenderTexture.active = initial;
        try
        {
            for (int i = 0; i < layers.Count; i++)
            {
                FaceLayer layer = layers[i];
                FacePose pose = layer.Parameter == 0 ? new FacePose() : poses[layer.Parameter - 1];
                material.SetTexture("_LayerTex", layer.Texture);
                material.SetTexture("_LayerMask", layer.Mask);
                material.SetFloat("_HasLayerMask", layer.Mask != null ? 1f : 0f);
                material.SetVector("_LayerPlacement", Placement(pose, layer.Right));
                material.SetVector("_LayerRotation", TextureRotation(pose, layer.Right));
                // Pupil and highlight are single images, not expression atlases.
                material.SetVector("_SourceGrid", layer.Parameter == 0 || layer.Parameter == 2 || layer.Parameter == 3
                    ? new Vector4(1f, 1f, 0f, 0f) : new Vector4(4f, 2f, 0f, 0f));
                material.SetVector("_AtlasGrid", new Vector4(4f, 2f, 0f, 0f));
                int selectedExpression = Mathf.Clamp(layer.Parameter == 5 ? mouthExpression : expression, 0, 7);
                material.SetVector("_ExpressionOffset", new Vector4(selectedExpression % 4, selectedExpression / 4, 0f, 0f));
                material.SetFloat("_LayerKind", layer.Parameter);
                material.SetColor("_SkinColor", QualitySettings.activeColorSpace == ColorSpace.Linear ? skin.linear : skin);
                Color primary = layer.Parameter == 0 ? RecipeColor(data, 0, lastMatch: true) :
                    layer.Parameter == 1 ? RecipeColor(data, 1, 3) :
                    layer.Parameter == 2 ? RecipeColor(data, 2, layer.Right ? 9 : 6) :
                    layer.Parameter == 4 ? RecipeColor(data, 4) :
                    layer.Parameter == 5 ? RecipeColor(data, 0) : Color.white;
                Color secondary = layer.Parameter == 0 ? primary : RecipeColor(data, 2, layer.Right ? 3 : 0);
                material.SetColor("_PrimaryColor", QualitySettings.activeColorSpace == ColorSpace.Linear ? primary.linear : primary);
                material.SetColor("_SecondaryColor", QualitySettings.activeColorSpace == ColorSpace.Linear ? secondary.linear : secondary);
                FaceLayer eye = layers.Find(value => value.Parameter == 1 && value.Right == layer.Right);
                material.SetTexture("_EyeMask", eye == null ? null : eye.Mask);
                material.SetFloat("_HasEyeMask", eye != null && eye.Mask != null ? 1f : 0f);
                material.SetVector("_EyePlacement", Placement(eyePose, layer.Right));
                material.SetVector("_EyeRotation", TextureRotation(eyePose, layer.Right));
                Graphics.Blit(a, b, material);
                RenderTexture swap = a; a = b; b = swap;
                if (layer.Parameter == 0 && (i + 1 == layers.Count || layers[i + 1].Parameter != 0))
                {
                    ApplyFeatureBase(owner, a, width / 4, height / 2);
                    // Keep the static feature on the head. Transparent expression
                    // backgrounds let it show through without moving with SP nodes.
                    RenderTexture previous = RenderTexture.active;
                    RenderTexture.active = a;
                    GL.Clear(true, true, empty);
                    RenderTexture.active = previous;
                }
            }
            RenderTexture old = RenderTexture.active; RenderTexture.active = a;
            var result = new Texture2D(width, height, TextureFormat.RGBA32, false)
            { name = "AvatarRecipeFacialParts", wrapMode = TextureWrapMode.Clamp };
            result.ReadPixels(new Rect(0, 0, width, height), 0, 0); result.Apply(false, false);
            RenderTexture.active = old;
            owner.GetComponent<RuntimeHeadResources>().Own(result);
            return result;
        }
        finally
        {
            foreach (FaceLayer layer in layers)
            {
                if (layer.Texture != null) Destroy(layer.Texture);
                if (layer.Mask != null) Destroy(layer.Mask);
            }
            Destroy(material); RenderTexture.ReleaseTemporary(a); RenderTexture.ReleaseTemporary(b);
        }
    }

    private static void ApplyFeatureBase(GameObject head, RenderTexture atlas, int width, int height)
    {
        Transform part = head.transform.Find("base_eye_10");
        if (part == null) throw new InvalidOperationException("Missing face base mesh.");
        // The head uses ordinary face UVs; expression meshes use the 4x2 atlas.
        // Keep one identical feature cell for the head so their seams agree.
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = atlas;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        { name = "AvatarRecipeFeatureBase", wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Bilinear };
        try { texture.ReadPixels(new Rect(0, 0, width, height), 0, 0); texture.Apply(false, false); }
        finally { RenderTexture.active = previous; }
        head.GetComponent<RuntimeHeadResources>().Own(texture);
        foreach (Material material in part.GetComponent<Renderer>().sharedMaterials)
        {
            material.mainTexture = texture;
            material.SetColor("_Color", Color.white);
            material.SetFloat("_WholeSkin", 0f);
            material.SetFloat("_HasMsk", 0f);
        }
    }
    private static void AddFaceLayers(System.Collections.Generic.List<FaceLayer> output, string folder, string name, int parameter)
    {
        if (string.IsNullOrEmpty(name)) return;
        try
        {
            string path = Path.Combine(DumpDataManager.Dx11Path, "chr", "_face", "20_EDIT", "_facetex", folder, name + ".g4tx");
            var warnings = new System.Collections.Generic.List<string>();
            var maps = G4TextureParser.Parse(File.ReadAllBytes(path), path, warnings, default, false);
            foreach (G4TextureParser.TextureData map in maps)
            {
                if (!G4TextureParser.IsBase(map.Name)) continue;
                G4TextureParser.TextureData maskMap = maps.Find(value =>
                    string.Equals(value.Name, map.Name + "msk", StringComparison.OrdinalIgnoreCase));
                output.Add(new FaceLayer
                {
                    Texture = CreateFaceTexture(map),
                    Mask = maskMap == null ? null : CreateFaceTexture(maskMap, true),
                    Parameter = parameter,
                    Resource = name,
                    Right = map.Name.IndexOf("_R_", StringComparison.OrdinalIgnoreCase) >= 0
                });
            }
        }
        catch (Exception exception)
        {
            Debug.LogWarning($"AvatarDemo failed to load facial texture {name}: {exception.Message}");
        }
    }

    private static Texture2D CreateFaceTexture(G4TextureParser.TextureData map, bool linear = false)
    {
        var texture = new Texture2D(map.Width, map.Height, map.Format, map.MipCount, linear)
        {
            name = map.Name,
            wrapMode = TextureWrapMode.Clamp,
            filterMode = FilterMode.Bilinear
        };
        texture.LoadRawTextureData(map.Pixels);
        texture.Apply(false, false);
        return texture;
    }

    private static void ApplyFacialPartsTexture(GameObject head, Texture2D texture)
    {
        if (head == null || texture == null) return;
        foreach (Renderer renderer in head.GetComponentsInChildren<Renderer>())
        {
            if (!renderer.name.StartsWith("parts_eye_", StringComparison.OrdinalIgnoreCase) &&
                !renderer.name.StartsWith("parts_mouth_", StringComparison.OrdinalIgnoreCase)) continue;
            foreach (Material material in renderer.materials)
            {
                material.mainTexture = texture;
                if (material.HasProperty("_Color")) material.SetColor("_Color", Color.white);
                if (material.HasProperty("_WholeSkin")) material.SetFloat("_WholeSkin", 0f);
                if (material.HasProperty("_HasMsk")) material.SetFloat("_HasMsk", 0f);
                if (material.HasProperty("_OcDarkness")) material.SetFloat("_OcDarkness", 1f);
                if (material.HasProperty("_SpmStrength")) material.SetFloat("_SpmStrength", 0f);
                float depthBias = renderer.name.StartsWith("parts_mouth_", StringComparison.OrdinalIgnoreCase) ? -2f : -1f;
                if (material.HasProperty("_DepthBiasFactor")) material.SetFloat("_DepthBiasFactor", depthBias);
                if (material.HasProperty("_DepthBiasUnits")) material.SetFloat("_DepthBiasUnits", depthBias);
            }
        }
    }
}





