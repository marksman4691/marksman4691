# 新增掃描機驅動

每個廠牌/SDK 實作一個 `IScannerDriver`。驅動**只負責取得原始資料**；
格式轉換 (X9 TIFF G4)、MICR 解析、品質檢查、需求檢查、取消/逾時鎖定都由共用程式處理。

## 1. 實作介面

```csharp
public sealed class EpsonTmsDriver : IScannerDriver
{
    public string Name => "Epson TM-S1000";
    public DriverCapabilities Capabilities { get; } = new(Front: true, Back: true, Micr: true, Batch: true);

    public Task<DriverStatus> GetStatusAsync(CancellationToken ct) { … }

    public async IAsyncEnumerable<RawItem> CaptureAsync(CaptureOptions options,
        [EnumeratorCancellation] CancellationToken ct)
    {
        // 1. 開啟裝置，設定 options.PreferredDpi (200)、黑白 G4 輸出
        // 2. 在 options.InsertionTimeout 內等待第一張；逾時丟
        //    new ScannerException("TIMEOUT", "...", StatusCodes.Status408RequestTimeout)
        // 3. 每張支票：
        yield return new RawItem(
            RawImage.FromTiff(frontBytes, dpi),   // 或 RawImage.FromGray(new GrayImage(...))
            RawImage.FromTiff(backBytes, dpi),
            NormalizeMicr(vendorMicr),            // 轉成標準字元，見下
            warnings);                            // 例如 "DOUBLE_FEED_DETECTED"
        // 4. options.Batch 為 false 時只回傳一張；為 true 時送到進紙槽空或 options.MaxItems
        // 5. 經常檢查 ct.ThrowIfCancellationRequested()，finally 中釋放裝置
    }
}
```

然後在 `Drivers/DriverFactory.cs` 註冊名稱，並在 `BridgeOptions` 加上該驅動的設定區段。

## 2. 影像

- 最好讓掃描機直接輸出 **黑白 CCITT G4 TIFF、200 dpi**：管線會原封不動保留像素，只重新包成標準 TIFF。
- 也可以回傳任何 TIFF (LZW/JPEG/未壓縮，灰階/彩色) 或 `GrayImage` 像素；管線會重新取樣到設定的 DPI 並黑白化。
- 其他格式 (JPEG/BMP) 請在驅動內解碼成 `GrayImage`。
- 正、背面都必須提供 (`RequireBack=true` 時缺背面會失敗)。

## 3. MICR 標準字元

| 標準字元 | E-13B 符號 | 說明 |
|---|---|---|
| `0`-`9` | | 數字 |
| `T` | ⑆ Transit | Routing 欄位前後 |
| `U` | ⑈ On-Us | |
| `A` | ⑇ Amount | |
| `D` | ⑉ Dash | |
| `?` | | 無法辨識 |
| 空白 | | 欄位間隔 (保留廠商的空白即可) |

各廠 SDK 的符號表示不同 (字母、控制碼或自訂字元)，請在驅動內轉換；依 SDK 文件建立對照表並寫單元測試。

## 4. COM / 執行緒

- 廠商 SDK 為 ActiveX / COM (STA) 時，參考 `Drivers/Opos/StaThread.cs`：所有呼叫都在同一條 STA 執行緒上，長時間等待時呼叫 `StaThread.PumpMessages()`。
- 原生 DLL (C API) 用 `[DllImport]`；注意 32/64 位元須與發佈的 Runtime (`win-x86` / `win-x64`) 一致。

## 5. 驗證清單

- [ ] `/api/device` 回報正確 capabilities，`unmetRequirements` 為空
- [ ] 單張：正反面影像、MICR 正確，沒有非預期警告
- [ ] 連續掃描 (若支援)：張數、順序正確；中途卡紙時已掃描的支票仍回傳
- [ ] 逾時、取消 (`/api/scan/cancel`、瀏覽器中斷) 後裝置可再次使用
- [ ] 裝置被其他程式占用、未連接、驅動未安裝時回傳對應錯誤碼
- [ ] 以 `tiffinfo` 或 JPM 提供的驗證工具檢查輸出影像
