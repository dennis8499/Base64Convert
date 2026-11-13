# Base64 檔案轉換工具

使用 .NET 10 與 Windows Forms 將檔案轉換為 Base64 文字分片，並將分片合併還原成檔案。程式以串流處理資料，不需要將整個檔案載入記憶體。

## 執行

需要 Windows 及 [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0)。在已安裝 .NET 10 SDK 的環境中，先建置方案：

```powershell
dotnet build Base64Convert.slnx -m:1
```

啟動程式：

```powershell
dotnet run --no-build --project src/Base64Convert.WinForms
```

執行不依賴額外測試套件的整合測試：

```powershell
dotnet run --no-build --project tests/Base64Convert.Tests
dotnet run --no-build --project tests/Base64Convert.UiSmokeTests
```

## 使用方式

1. 在「轉換與拆分」選取來源檔案及輸出資料夾，設定每個文字分片的上限，然後開始轉換。預設為 15 MB，1 MB 等於 1,000,000 位元組。實際分片大小會向下對齊 4 位元組。
2. 可選擇產生驗證資訊檔（預設開啟）。若要讓另一位使用者驗證還原結果，請將資訊檔與全部文字分片一併傳送。
3. 在「合併與還原」選取同一組的全部文字分片。程式會在第一個分片所在的資料夾尋找對應資訊檔，也可手動選擇或清除資訊檔。接著選擇還原的儲存位置。

文字分片的檔名包含隨機識別碼、分片編號與總數；請保留檔名，避免不同轉換作業的分片混在一起。資訊檔包含原始檔名、大小及 SHA-256，還原時會驗證檔案。沒有資訊檔仍可還原及檢查分片順序，但無法驗證內容是否遭修改。若原始檔名非常長，分片檔名中的名稱可能縮短；沒有資訊檔時，請在還原對話框確認檔名。

大小上限僅適用於產生的文字檔，不包含郵件附件再次編碼、郵件標頭或內容；實際郵件大小可能更大。使用驗證資訊檔可偵測意外損毀，但若資訊檔與分片都遭竄改，它不能證明檔案來源或真實性。
