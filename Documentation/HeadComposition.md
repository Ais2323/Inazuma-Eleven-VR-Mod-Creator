# 頭部拼貼與角色匯出

## 使用方式

1. 從 StartScene 啟動。有效的 Dump 路徑會自動進入 CharacterPreviewDemo。
2. 點選右上角「部件拼貼 / Compose」。左側選擇來源角色。
3. 勾選整個 mesh 或個別連通塊，再按「加入拼貼」。可切換來源，繼續加入其他角色的部件。
4. 右側選取拼貼項目，可命名、移除、編輯 XYZ 位置／旋轉／縮放或重設。縮放必須大於零。
5. 選擇要替換的既有角色。
6. 按「選擇資料夾並匯出」。每次都重新開啟資料夾選擇器；取消不寫檔。成果位於新建子資料夾，既有成果不會被覆蓋。

「顯示」只影響預覽；拼貼清單內所有部件都會匯出。拖曳旋轉與相機置中不改變匯出座標。
連通塊依共用頂點索引分組，不焊接相同位置。幾何相連的頭髮與臉無法進一步切開。
「儲存拼貼文件」可保存來源、原始 mesh／三角形、變換與手動骨骼對應；讀取時仍需原 Dump。

## 輸出內容

- `common/chr/_face/.../*.g4md`、`.g4mg`。
- `dx11/.../*.g4tx`。替換沿用解析到的目標貼圖容器路徑，保留容器原有貼圖，避免影響其他模型。
- `composition.json`：本次請求及部件來源。
- `report.md`：來源 SHA256、骨架依據、實際骨骼映射，以及驗證範圍。

來源 Dump 保持唯讀。輸出路徑使用 Windows 實際路徑解析，拒絕 Dump 本身、子目錄與指向它們的 junction／symbolic link。
檔案先寫入本次專用暫存目錄，回讀驗證後才發布。取消或失敗只清理本次暫存目錄。

## 格式與限制

- runtime 使用 C#，不啟動 Blender、Python 或 VictoryTool，也不引用 VictoryTool 的 .NET 10 DLL。
- 保留原始頂點屬性、材質參數、render state、sampler、所有引用貼圖與 DDS mipmaps。
- 完整保留目標 G4MD 的 joint table 與索引順序。來源 joint hash 存在於目標時沿用對應索引；目標沒有的髮骨會合併至目標頭部 joint。角色共用骨架由遊戲既有設定解析；工具不讀取、不建立也不輸出 G4SK。
- 相容替換會保留目標 G4MD 的 layout、材質、hash、名稱及私有尾端表格，只修補 mesh geometry record、bounds 與 G4MG buffer。第一版要求拼貼後的主頭部／眼睛／嘴巴 mesh 數量與目標相同，且 vertex layout 可由目標原檔提供。
- 混合來源的 mesh palette 數量可以不同。工具會在目標原生 palette 表的既有容量內重新配置每個 mesh 的 start／count；只有合併後確實超過原表容量才停止匯出。
- 三個 mesh 都來自同一角色時，相容替換會沿用該來源角色的原生材質表與完整 G4TX，並把檔案放在目標角色路徑。多來源材質與貼圖容器合併仍屬後續範圍。
- 原生 G4MD 寫入目前驗證的 `0xA0 / 0x68` 格式及 position float3、8 組 U16 權重／U8 骨骼索引；未知有效區段不猜測重寫。
- 目前只輸出替換既有角色，不輸出新增角色資料表、商店資料或頭像。
- 此功能沒有原遊戲實機驗收；格式回讀與 Unity 顯示成功不代表已驗證遊戲中的動畫、取得流程或重啟辨識。
- 不使用原生 Avatar、不修改存檔、不修改全域成長表、不安裝 CPK。

## 程式入口

- `Assets/Scripts/Composition/CompositionEditor.cs`：拼貼 uGUI 與生命週期。
- `CompositionDocument.cs`：文件、部件與匯出請求。
- `RawHeadAsset.cs`：原始模型、材質與貼圖資料解析。
- `G4CompositionWriter.cs`、`CompositionExporter.cs`：原生格式、路徑保護與發布。

解析參考本機 `G:\G4_Blender-main` 與 `G:\VictoryTool-main`。BC7 mode 6 依 [Microsoft 格式規格](https://learn.microsoft.com/en-us/windows/win32/direct3d11/bc7-format)實作。

