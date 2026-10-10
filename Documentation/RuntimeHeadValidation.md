# 頭部預覽驗證紀錄

日期：2026-09-29；Unity：6000.3.10f1；Windows / Direct3D 11。

## 已驗證

- C# 編譯與 Unity Editor 編譯通過。
- 12 個系列各取首、中、末樣本，共 36 個模型：網格解析成功；截斷 G4MD/G4MG 與取消請求均被正確處理。
- 以上 36 個樣本的 mesh、vertex、triangle 數量，與 `G4_Blender-main/g4_model_probe.py` 直接執行的結果一致（COMBO 比較時同樣只保留頭部記錄）。
- 12 個系列各一個樣本：透過真正的 Unity runtime loader 建立 Mesh、Material、Texture2D，渲染 PNG。已檢查一般頭部的正面、臉部 UV 與材質，以及 COMBO 頭部篩選結果。
- 合成 DDS 檢查：DXGI 71、77、98、28、87；截斷像素、未知格式、壞 G4TX、越界三角形都會回報錯誤。
- Play Mode：直接開 Demo 且無效設定會返回 StartScene；設定有效 Dump 後進入 Demo；快速切換會取消舊請求；返回 StartScene 時有效設定會再次自動進 Demo；直接重新開 Demo 可正常載入。
- 下拉選單列出 6,067 個成對模型；展開後項目數一致，並檢查實際展開畫面。
- 場景切換後僅保留一個 RuntimeHeadResources 根物件。
- Windows Development Build 與自動場景 smoke test 通過。

## 資料限制

- `20_EDIT` 的部分 accessory / hair 零件缺少同名貼圖，會顯示灰色與警告；沒有替它們推測其他角色的貼圖。
- COMBO 資料包含兩個人物；目前保留兩個頭部的原始位置，移除球衣、鞋子與球。
- 這是抽樣驗證，並非全部 6,067 個模型都經過視覺檢查。
- 未加入骨架、表情、編輯角色零件組裝或原作完整 shader。

## 本機產物

- `Logs/HeadValidation/`：各系列模型 PNG 與二進位格式驗證結果。
- `Logs/HeadPlayMode-Final/`：Play Mode 場景/UI 測試結果和截圖。
- `Logs/HeadSmoke-Final/`：最終 Windows Player 測試結果和截圖。
- `Builds/HeadDemo/HeadDemo.exe`：Windows Development 測試版。
- `Temp/HeadValidationProject/`：隔離的驗證專案；不影響原專案已開啟的 Editor。

這些本機測試產物位於現有 Git ignore 範圍。測試入口與重現方式見 `RuntimeHeadPreview.md`。
