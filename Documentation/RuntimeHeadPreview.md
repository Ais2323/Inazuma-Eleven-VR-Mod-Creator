# Runtime 頭部預覽

## 使用

- 開啟 `Assets/Scenes/StartScene.unity` 並進入 Play Mode。已儲存的 Dump 有效時自動切換 `CharacterPreviewDemo`。
- 也可直接開啟 `Assets/Scenes/CharacterPreviewDemo.unity`；無效設定會返回 StartScene。
- Demo 的 UI、鏡頭與 EventSystem 在執行時建立。下拉選單顯示 `_face` 相對路徑，拖曳旋轉、滾輪縮放。
- Build Settings 已加入 StartScene（第一個）與 CharacterPreviewDemo。
- 支援 Windows、Built-in render pipeline。需要 GPU 支援來源 DDS 壓縮格式（目前角色通常為 BC7）。

## API

請從 Unity 主執行緒呼叫，並保留 Unity SynchronizationContext：

```csharp
if (!DumpDataManager.ValidateSavedPath())
    throw new System.InvalidOperationException(DumpDataManager.LastError);

var loader = new G4Runtime.HeadModelLoader();
var heads = await loader.GetHeadsAsync(cancellationToken);
var result = await loader.LoadHeadAsync(heads[0], cancellationToken);
result.Root.transform.SetParent(parent, false);
// result.Warnings 包含缺圖或不支援貼圖資訊。
// 不再使用時 Destroy(result.Root)，其 RuntimeHeadResources 會釋放網格、材質和貼圖。
```

Loader 建立時固定使用當下已驗證的資料夾。更新 Dump 後重新建立 loader 與清單。來源檔案只讀；没有磁碟轉檔快取。

## 解析範圍

- G4MD 的 mesh/layout/material/hash 表，以及 G4MG 的位置、SNORM16 法線、UV、頂點色、16-bit 三角形索引。
- G4TX 內 2D DDS：BC1、BC3、BC7、RGBA32/BGRA32，包含 DX10 header 與 mip chain。
- Z 軸鏡射與三角形繞序轉換；DDS 保留原始列順序，UV 同樣保留原始 V 值。
- 頂點色保留在 Mesh，預覽 shader 只顯示基本貼圖與簡單光照；遊戲原始頂點色可能承載專用 shader 資料，未直接當作顏色乘數。
- 貼圖先依材質引用的 CRC32 選擇，再依名稱尋找。只載入主色貼圖，缺少時顯示灰色並回報原因。
- `_LOD` 名稱取最低編號。檔案必須是獨立的 G4MD/G4MG 配對。`22_COMBO` 的球衣、鞋子與球會過濾掉；兩個頭部保留原檔位置。
- 不包含骨架、表情、原作描邊或完整材質、G4PK 解包、編輯角色零件自動組裝。
- 參考本機 `G:\G4_Blender-main\g4_model_probe.py` 的格式解析及 `shading/character_textures.py` 命名規則。

## 驗證入口

- Unity 選單 `Tools > G4 > Validate sample heads`：各系列抽一個模型，執行 runtime loader、渲染 PNG、檢查取消，輸出至 `Logs/HeadValidation`。此入口會建立暫時物件，建議在空白場景使用。
- Batch entry `G4HeadValidation.BuildWindows`：建立 Windows Development Player；環境變數 `G4_BUILD_OUTPUT` 可指定 exe 路徑。
- Development Player 可加上 `--g4-smoke "輸出資料夾" "Dump根目錄"` 執行場景、下拉選單、快速切換及場景資源清理測試。測試設定僅寫入指定輸出資料夾，不使用使用者的持久設定。
- `G4_VALIDATION_DUMP` 和 `G4_VALIDATION_OUTPUT` 可指定 batch 模型驗證的 Dump 與輸出路徑。

