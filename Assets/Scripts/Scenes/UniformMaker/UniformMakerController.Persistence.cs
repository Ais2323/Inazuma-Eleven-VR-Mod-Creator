using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SFB;
using UnityEngine;
using UnityEngine.UI;

public sealed partial class UniformMakerController
{
    public Button saveButton, loadButton;

    void InitializePersistence()
    {
        if (saveButton != null) { saveButton.interactable = false; saveButton.onClick.AddListener(OnClickSave); }
        if (loadButton != null) { loadButton.interactable = false; loadButton.onClick.AddListener(OnClickLoad); }
    }

    public void OnClickSave()
    {
        if (catalog == null || selected < 0) return;
        string name = isNewUniform && uniformNameInput != null ? uniformNameInput.text : catalog.Entries[selected].Key;
        name = new string((name ?? "uniform").Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        string path = StandaloneFileBrowser.SaveFilePanel("Save UniformMaker", "", name, "uniformmaker");
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!path.EndsWith(".uniformmaker", StringComparison.OrdinalIgnoreCase)) path += ".uniformmaker";
        try { CreateDocument().Write(path); Debug.Log("UniformMaker saved: " + path); }
        catch (Exception error) { Debug.LogError("[UniformMaker] " + error.Message, this); }
    }

    public void OnClickLoad()
    {
        var paths = StandaloneFileBrowser.OpenFilePanel("Load UniformMaker", "", "uniformmaker", false);
        if (paths == null || paths.Length == 0 || string.IsNullOrWhiteSpace(paths[0])) return;
        try { RestoreDocument(UniformMakerDocument.Read(paths[0])); }
        catch (Exception error) { Debug.LogError("[UniformMaker] " + error.Message, this); }
    }

    internal UniformMakerDocument CreateDocument()
    {
        if (catalog == null || selected < 0) throw new InvalidOperationException("Select a uniform first.");
        var entry = catalog.Entries[selected];
        var doc = new UniformMakerDocument { targetUniformId = isNewUniform ? 0 : entry.Id, targetKey = entry.Key,
            isNewUniform = isNewUniform, templateUniformId = entry.Id, newUniformKey = newUniformKey,
            uniformName = uniformNames.Find(n => n.locale == nameLocale)?.name,
            names = uniformNames.Select(n => new UniformLocalizedName { locale = n.locale, name = n.name }).ToList(),
            iconPngBase64 = uploadedIcon == null ? null : Convert.ToBase64String(uploadedIcon),
            freeInShop = freeShopToggle != null && freeShopToggle.isOn,
            side = side, role = role, bodyType = bodyType, collar = collar, sleeve = sleeve, hem = hem,
            chestSize = chestSize, number = uniformNumber, skinColor = SelectedSkinColor };
        foreach (var pair in kitEdits.Where(p => p.Key.StartsWith(entry.Id + "/", StringComparison.Ordinal)).OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            string[] key = pair.Key.Split('/'); var edit = pair.Value;
            var variant = new UniformMakerVariant { side = int.Parse(key[1]), role = int.Parse(key[2]),
                clothesModelId = edit.Clothes, armbandModelId = edit.Armband, gloveModelId = edit.Gloves };
            foreach (var texture in edit.Textures.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                string[] channel = texture.Key.Split('/'); var value = texture.Value;
                variant.textures.Add(new UniformMakerTexture { part = (UniformPartKind)Enum.Parse(typeof(UniformPartKind), channel[0]),
                    channel = int.Parse(channel[1]), originalName = value.OriginalName, fileName = Path.GetFileName(value.Path),
                    extension = Path.GetExtension(value.Path).ToLowerInvariant(), width = value.Texture.width, height = value.Texture.height,
                    mipCount = value.MipCount, bytesBase64 = Convert.ToBase64String(value.SourceBytes) });
            }
            doc.variants.Add(variant);
        }
        doc.Validate(); return doc;
    }

    internal void RestoreDocument(UniformMakerDocument doc)
    {
        doc.Validate();
        if (catalog == null) throw new InvalidOperationException("Uniform data is not loaded.");
        uint template = doc.isNewUniform ? doc.templateUniformId : doc.targetUniformId;
        int index = catalog.Entries.FindIndex(e => e.Id == template);
        if (index < 0) throw new InvalidDataException("The saved uniform is missing from this game data: " + doc.targetKey);
        var prepared = new Dictionary<string, KitEdit>();
        try
        {
            foreach (var variant in doc.variants)
            {
                var edit = new KitEdit { Clothes = variant.clothesModelId, Armband = variant.armbandModelId, Gloves = variant.gloveModelId };
                prepared.Add(template + "/" + variant.side + "/" + variant.role, edit);
                foreach (var saved in variant.textures)
                {
                    byte[] bytes = Convert.FromBase64String(saved.bytesBase64);
                    var texture = UniformTextureCodec.Decode(bytes, saved.extension, saved.width, saved.height, saved.channel != 0);
                    texture.name = saved.originalName;
                    edit.Textures.Add(saved.part + "/" + saved.channel, new TextureEdit { OriginalName = saved.originalName,
                        Texture = texture, SourceBytes = bytes, Path = saved.fileName, MipCount = saved.mipCount });
                }
            }
        }
        catch { foreach (var edit in prepared.Values) foreach (var texture in edit.Textures.Values) DestroyAsset(texture.Texture); throw; }
        CloseModelSelect(); ClearPreview();
        DisposePartEditor();
        foreach (var pair in prepared) kitEdits.Add(pair.Key, pair.Value);
        selected = index; side = doc.side; role = doc.role; bodyType = doc.bodyType;
        isNewUniform = doc.isNewUniform; newUniformKey = doc.newUniformKey;
        RestoreUniformInfo(doc);
        if (freeShopToggle != null) freeShopToggle.SetIsOnWithoutNotify(doc.freeInShop);
        collar = doc.collar; sleeve = doc.sleeve; hem = doc.hem; chestSize = doc.chestSize; uniformNumber = doc.number;
        if (skinColor != null) skinColor.color = doc.skinColor;
        appliedSkinColor = doc.skinColor;
        selections.Clear(); NormalizeFashion(); LoadPreview(); RefreshButtons();
    }
}
