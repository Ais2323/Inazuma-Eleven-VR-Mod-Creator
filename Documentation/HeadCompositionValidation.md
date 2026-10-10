# 拼貼功能驗證紀錄

環境：Unity 6000.3.10f1、Built-in、Windows x64。驗證使用隔離專案 `Logs/CompositionProject`，來源為 `D:\備份\data\data`。

## 已驗證

- Editor Play Mode：從兩個來源加入 mesh／連通塊；快速切換取消舊請求；XYZ 欄位更新；拒絕負縮放；移除部件；切換後釋放未使用來源。
- Windows development build：成功建立並執行拼貼與替換匯出測試。
- 保守替換沿用單一來源角色的原生 layout、材質、hash、名稱與私有表格，再將 joint／palette 改為目標索引。輸出 G4TX 與來源角色逐位元相同，且不含 G4SK。
- G4MD／G4MG／G4TX：Unity 回讀與 Blender 參考解析器獨立讀取；多材質、palette、三角形數量與必要 DDS 保留。
- 12 個系列各抽查一個頭部：11 個通過；20_EDIT/accessory001 因缺少必要貼圖 398C8EC9 而明確阻止匯出。
- 原始模型貼圖 DDS 與來源保持位元組一致。
- 路徑保護：Dump 根目錄、子目錄及 junction 目標均被拒絕。
- 錯誤處理：空拼貼、負縮放與缺少必要貼圖會被拒絕。
- 取消後不產生輸出資料夾；驗證期間沒有遺留暫存目錄。每次匯出對實際使用的來源模型、貼圖、骨架及資料表驗證 SHA256。

## 驗證入口與產物

- `Assets/Editor/G4CompositionValidation.cs`：`Run`、`Edges`、`Series`。
- `Assets/Scripts/Composition/G4CompositionSmokeTest.cs`：僅在 Editor／development build 且指定 `--g4-composition-smoke <output> <dump>` 時啟用。
- `Logs/CompositionPlayModeFinal/smoke-result.txt`、`Logs/CompositionStandaloneFinal/smoke-result.txt`。
- `Logs/CompositionValidation/independent-validation.txt`、`edge-validation.txt`、`series-validation.txt`。
- 參考解析腳本位於 `Logs/CompositionTools`，只用於開發驗證，runtime 沒有 Python 依賴。
- Windows 執行檔：`Builds/CompositionDemo/CompositionDemo.exe`。完整執行資料在同資料夾，請勿只移動 exe。

## 尚未驗證的範圍

未在原遊戲安裝或執行成果，因此沒有宣稱遊戲中的取得流程、動畫、表情、頭像顯示或重啟辨識已通過。未知資料表版本與未支援的原生結構會停止匯出。雙層頭像使用相同拼貼結果，不自動去除後髮。

尚未以實際磁碟故障或權限撤銷測試寫入中途失敗，也未進行完整遊戲骨骼姿勢的視覺驗收。此紀錄只代表上述格式與工具內測試。
