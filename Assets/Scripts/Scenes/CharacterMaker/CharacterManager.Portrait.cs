using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using G4Composition;
using G4Runtime;
using UnityEngine;

public partial class CharacterManager : MonoBehaviour
{
    private byte[] BuildCharacterPortrait(string characterId)
    {
        if (charIconCamera == null)
            throw new InvalidDataException("CharIconCamera 未設定。");
        RenderTexture target = charIconCamera.targetTexture;
        if (target == null)
            throw new InvalidDataException("CharIconCamera 沒有設定 RenderTexture。");
        if (!hasTargetFaceAnchor)
            throw new InvalidDataException("尚未取得替換角色的臉部高度，無法輸出頭像。");

        string templatePath = Path.Combine(
            DumpDataManager.Dx11Path, "menu", "200_icon", "10_icon_chr", "face",
            characterId + "_l.g4tx");
        if (!File.Exists(templatePath))
            throw new FileNotFoundException("找不到角色頭像範本：" + templatePath, templatePath);

        byte[] template = File.ReadAllBytes(templatePath);
        List<RawTexture> layers = RawTexture.Read(template, templatePath);
        if (layers.Count != 2)
            throw new InvalidDataException(templatePath + "：頭像 G4TX 必須包含兩張貼圖。");

        int width = ReadPortraitDimension(layers[0].Dds, 16, layers[0].Name, "寬度");
        int height = ReadPortraitDimension(layers[0].Dds, 12, layers[0].Name, "高度");
        if (width != 256 || height != 256 || target.width != width || target.height != height)
            throw new InvalidDataException(
                "頭像範本與 CharIcon RenderTexture 必須都是 256x256。範本：" +
                width + "x" + height + "，RenderTexture：" + target.width + "x" + target.height + "。");

        for (int i = 1; i < layers.Count; i++)
        {
            int layerWidth = ReadPortraitDimension(layers[i].Dds, 16, layers[i].Name, "寬度");
            int layerHeight = ReadPortraitDimension(layers[i].Dds, 12, layers[i].Name, "高度");
            if (layerWidth != width || layerHeight != height)
                throw new InvalidDataException(templatePath + "：兩張頭像貼圖尺寸不一致。");
        }

        byte[] front = null, rear = null;
        CapturePortraitLayers((render, isFront) =>
        {
            byte[] dds = CapturePortraitDds(charIconCamera, render, width, height);
            if (isFront) front = dds; else rear = dds;
        });
        byte[] output = ReplacePortraitDdsObject(template, layers, 0, rear, templatePath);
        List<RawTexture> updatedLayers = RawTexture.Read(output, "portrait rear output");
        return ReplacePortraitDdsObject(output, updatedLayers, 1, front, templatePath);
    }

    internal bool HasPortraitTarget => hasTargetFaceAnchor && charIconCamera != null && charIconCamera.targetTexture != null;

    // The live icon and exported portrait use the same framing and depth slices.
    internal bool CapturePortraitLayers(Action<RenderTexture, bool> capture)
    {
        if (!HasPortraitTarget) return false;
        Vector3 originalPosition = charIconCamera.transform.position;
        RenderTexture previousActive = RenderTexture.active;
        try
        {
            RenderTexture target = charIconCamera.targetTexture;
            if (!target.IsCreated()) target.Create();
            if (iconController != null && iconController.HasUploadedPortrait)
            {
                iconController.CaptureUploadedPortrait(target, capture);
                return true;
            }
            Vector3 position = originalPosition;
            position.y = targetFaceAnchor.y + portraitCameraHeightOffset;
            position.z = -1f;
            charIconCamera.transform.position = position;
            charIconCamera.Render(); capture(target, true);
            position.z = 0f;
            charIconCamera.transform.position = position;
            charIconCamera.Render(); capture(target, false);
            return true;
        }
        finally
        {
            charIconCamera.transform.position = originalPosition;
            RenderTexture.active = previousActive;
        }
    }

    private static int ReadPortraitDimension(byte[] dds, int offset, string name, string field)
    {
        if (dds == null || dds.Length < 128 ||
            dds[0] != (byte)'D' || dds[1] != (byte)'D' ||
            dds[2] != (byte)'S' || dds[3] != (byte)' ')
            throw new InvalidDataException(name + " 不是有效的 DDS。");
        int value = BitConverter.ToInt32(dds, offset);
        if (value <= 0) throw new InvalidDataException(name + " 的 DDS " + field + "無效。");
        return value;
    }

    private static byte[] CapturePortraitDds(Camera camera, RenderTexture target, int width, int height)
    {
        Texture2D capture = null;
        try
        {
            if (!target.IsCreated()) target.Create();
            RenderTexture.active = target;
            capture = new Texture2D(width, height, TextureFormat.RGBA32, false, true);
            capture.ReadPixels(new Rect(0, 0, width, height), 0, 0, false);
            capture.Apply(false, false);

            Color32[] unityPixels = capture.GetPixels32();
            if (!target.sRGB)
            {
                for (int i = 0; i < unityPixels.Length; i++)
                {
                    Color32 pixel = unityPixels[i];
                    pixel.r = LinearByteToSrgb(pixel.r);
                    pixel.g = LinearByteToSrgb(pixel.g);
                    pixel.b = LinearByteToSrgb(pixel.b);
                    unityPixels[i] = pixel;
                }
            }
            var ddsPixels = new Color32[unityPixels.Length];
            for (int y = 0; y < height; y++)
                Array.Copy(unityPixels, y * width, ddsPixels, (height - 1 - y) * width, width);
            return Bc7PortraitEncoder.Encode(ddsPixels, width, height, CancellationToken.None);
        }
        finally
        {
            if (capture != null)
            {
                if (Application.isPlaying) Destroy(capture);
                else DestroyImmediate(capture);
            }
        }
    }

    private static byte LinearByteToSrgb(byte value)
    {
        float linear = value / 255f;
        return (byte)Mathf.Clamp(Mathf.RoundToInt(Mathf.LinearToGammaSpace(linear) * 255f), 0, 255);
    }

    private static byte[] ReplacePortraitDdsObject(
        byte[] template,
        IReadOnlyList<RawTexture> layers,
        int layerIndex,
        byte[] replacement,
        string path)
    {
        if (layerIndex < 0 || layerIndex >= layers.Count)
            throw new InvalidDataException(path + "：指定的頭像圖層不存在。");

        var reader = new G4ModelParser.Binary(template, path);
        int header = reader.U16(4);
        int count = reader.U16(0x20);
        int payload = checked((header + reader.Int(12) + 15) & ~15);
        if (header != 0x60 || count != layers.Count)
            throw new InvalidDataException(path + "：不支援的頭像 G4TX 結構。");

        int descriptor = header + layerIndex * 48;
        int objectOffset = checked(payload + reader.Int(descriptor + 4));
        int objectSize = reader.Int(descriptor + 8);
        if (replacement.Length != objectSize || layers[layerIndex].Dds.Length != objectSize)
            throw new InvalidDataException(
                layers[layerIndex].Name + "：編碼後 DDS 大小與原生頭像槽位不同（" +
                replacement.Length + " / " + objectSize + " bytes）。");
        reader.Range(objectOffset, objectSize);

        byte[] output = (byte[])template.Clone();
        Buffer.BlockCopy(replacement, 0, output, objectOffset, objectSize);

        List<RawTexture> check = RawTexture.Read(output, "portrait output");
        for (int i = 0; i < layers.Count; i++)
        {
            if (!check[i].Descriptor.SequenceEqual(layers[i].Descriptor) ||
                check[i].Name != layers[i].Name || check[i].Hash != layers[i].Hash)
                throw new InvalidDataException("頭像 G4TX 的原生描述資料在輸出時被改變：" + layers[i].Name);
            byte[] expected = i == layerIndex ? replacement : layers[i].Dds;
            if (!check[i].Dds.SequenceEqual(expected))
                throw new InvalidDataException("頭像 G4TX 回讀驗證失敗：" + layers[i].Name);
        }
        return output;
    }

}
