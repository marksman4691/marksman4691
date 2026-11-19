# Check Scanner Bridge

在 Windows 上執行的本機服務，作為 **網頁 (瀏覽器)** 與 **支票掃描機** 之間的橋樑：
網頁呼叫 bridge 啟動掃描，取得每張支票的 **正反面 X9 影像 (TIFF G4)** 與 **MICR 解析結果**，
上傳到自己的後端後，由後端組成 X9 檔案經 **JPM Host-to-Host** 交換。

設計重點是 **支援多種機型**：每個廠牌只需實作一個「驅動」，
影像格式轉換、MICR 解析、品質檢查都在共用管線中處理，輸出格式與機型無關。

```
 網頁 (checkscan-bridge.js)
   │  HTTP  http://127.0.0.1:9123   (Origin 白名單、選用 API Key)
   ▼
 ┌──────────────────────── CheckScanBridge (Windows 服務) ────────────────────────┐
 │ CaptureService ── 單一作業鎖、取消、逾時、需求檢查 (正反面 / MICR)              │
 │      │                                                                       │
 │      ▼  IScannerDriver (每個廠牌一個)          ItemProcessor (與機型無關)       │
 │  ┌──────────────┐  RawItem: 正/背面原始影像   ┌─────────────────────────────┐ │
 │  │ Mock         │  + MICR (標準字元 T/U/A/D) ─▶│ 解碼 TIFF / 灰階              │ │
 │  │ EpsonWebApi ─┼─ LAN HTTP(S) ─▶ TM-S1000II-NW │ → 重新取樣到 200 dpi          │ │
 │  │ Panini       │                             │ → 黑白化 (原生黑白直接使用)    │ │
 │  │ Digital Check│                             │ → X9 TIFF G4 + 格式自我驗證   │ │
 │  │ OPOS (通用)  │                             │ → MICR 解析 + routing 檢查碼  │ │
 │  └──────────────┘                             │ → 基本影像品質檢查 → 警告      │ │
 │                                               └─────────────────────────────┘ │
 └───────────────────────────────────────────────────────────────────────────────┘
   │ 回傳 items[]: front/back (TIFF G4 + PNG 預覽), micr, warnings
   ▼
 網頁 → FormData 上傳 → 你的後端 → 組 X9.37 / X9.100-187 檔案 → JPM Host-to-Host
```

## 需求對照

| 需求 | 做法 | 狀態 |
|---|---|---|
| 正反面影像 | `RequireBack=true`；驅動不支援背面時拒絕掃描 (`CAPABILITY_MISSING`) | ✅ 管線完成；實機待驅動 |
| 符合 JPM H2H 影像格式 | 輸出 X9 通用規格：TIFF 6.0、單頁、單一 strip、1-bit、CCITT G4、WhiteIsZero、200 dpi；DPI 與 byte order 可設定，輸出後自我驗證 | ✅ 待 JPM 規格書確認細節 |
| MICR (X9 Type 25 需要) | 解析 Routing (含檢查碼)、On-Us (`/` 表示法)、Aux On-Us、EPC、已印金額 | ✅ |
| 連續掃描 (加分) | `batch: true`；中途出錯時已掃描的支票仍會回傳 | ✅ 管線完成；需驅動支援 |
| 多機型 | `IScannerDriver` 驅動介面 + 共用處理管線；依設定切換 `Driver` | ✅ 架構完成 |

### 驅動狀態

| Driver | 正面 | 背面 | MICR | 連續 | 說明 |
|---|---|---|---|---|---|
| `EpsonWebApi` | ✅ | ✅ | ✅ | ✅ | **Epson 網路機種 (TM-S1000II-NW)**，透過掃描機內建 Scan Web API，免安裝驅動。依 Epson Web API Reference Guide Rev.A 實作，**待實機驗證** |
| `Mock` | ✅ | ✅ | ✅ | ✅ | 模擬裝置，網頁端開發與測試用 |
| `Opos` | ✅ | ❌ | ❌ | ❌ | UPOS 標準未定義背面影像，**不符合需求**，僅保留作參考 |
| Epson USB / Panini / Digital Check | | | | | 視需要，依各廠 SDK 實作 |

新增驅動的方式見 [docs/adding-a-driver.md](docs/adding-a-driver.md)。

## 為什麼不用 TWAIN

- 瀏覽器無法直接呼叫 TWAIN，用 TWAIN 一樣需要本機 bridge。
- TWAIN 是通用影像傳輸協定，**沒有標準化**支票專用功能 (MICR、每張支票的正反面配對、進紙/退紙控制、背書列印)；
  各廠商以自訂 capability 實作，行為不一致，有些機型透過 TWAIN 讀不到 MICR。
- RDC / 銀行端軟體 (例如 JPM Remote Capture 使用的掃描元件) 也是透過支票掃描機專用 API，而非 TWAIN。

## Epson TM-S1000II-NW (EpsonWebApi 驅動)

掃描機內建 Scan Web API，bridge 透過區域網路呼叫，**電腦不需安裝 Epson 驅動**。
掃描機直接輸出 200 dpi 黑白 CCITT G4 TIFF (Epson 標示符合 ANSI X9.100-181)，並在機器上完成 MICR 讀取與 IQA。

流程：`Connect` (取得 token、獨占掃描機) → `ScanSetting(Check)` (正反面、TIFF 黑白、MICR E13B、IQA)
→ `ScanStart(Check)` → 輪詢 `GetDocumentList` → `GetImage` 正/背面 → `Disconnect` (掃描機刪除所有影像)。

### 掃描機設定

1. 用 TM-S1000II Utility / Web Config 設定網路，**建議設為固定 IP** (或 DHCP 保留)。
   Web Config 預設密碼是機器序號，請改掉。
2. 瀏覽器開 `http://<掃描機IP>/demo` 可用 Epson 內建的掃描示範確認機器正常。
3. 設定 `appsettings.json`：

```jsonc
"Bridge": {
  "Driver": "EpsonWebApi",
  "EpsonWebApi": {
    "BaseUrl": "http://192.168.1.50",       // 掃描機 IP；建議改用 HTTPS (見下)
    "ExclusiveTimeoutSeconds": 60,
    "IqaEnabled": true,                      // 掃描機 IQA 未通過的項目成為 IQA_* 警告
    "StopOnMicrError": false,
    "ElectronicEndorsement": ""              // 選用：電子背書文字 (印在背面影像)
  }
}
```

### 注意事項

- **獨占使用**：同一時間只有一台電腦能連線 (`Connect`)；其他電腦會收到 `BUSY`。bridge 每次掃描結束 (含取消、
  錯誤、瀏覽器中斷) 都會立即 `Disconnect` 釋放掃描機；異常中斷時掃描機會在 `ExclusiveTimeoutSeconds` 後自動釋放。
- **傳輸安全**：HTTP 會在區網以明文傳送支票影像。正式環境建議在 Web Config 註冊伺服器憑證啟用 HTTPS
  (憑證 Common Name 須等於掃描機主機名稱)，`BaseUrl` 用 `https://主機名稱`，並把簽發 CA 安裝到電腦的「受信任的根憑證」。
- **MICR 符號**：Epson MICR 文字以 `t` (Transit)、`o` (On-Us) 表示特殊符號 (手冊範例)；Amount / Dash 預設對應 `a` / `-`，
  手冊未明載，以 `MicrSymbolMap` 設定。驅動會用 Epson 自己解析的 routing 交叉檢查，不一致時產生 `MICR_PARSE_MISMATCH` 警告。
- **Authorization header**：手冊寫「放入 Connect 取得的 token」，實作為直接放 token；若實機要求 `Bearer`，設定 `AuthorizationScheme`。
- 掃描機也支援瀏覽器直接呼叫 (Epson 範例程式即是)，但公司 HTTPS 網站呼叫區網掃描機需每台掃描機的 HTTPS 憑證、
  Chrome 區域網路權限，且 MICR/格式處理要搬到網頁端；因此仍建議透過 bridge。

## 影像格式與 JPM Host-to-Host

預設輸出依 X9.37 / X9.100-187 (影像格式為 X9.100-181) 常見要求。**各銀行規格有差異** (例如有銀行要求 big-endian)，
請向 JPM 取得 Host-to-Host 影像交換規格，確認以下項目並調整 `Bridge:Image` 設定：

- [ ] 解析度 (200 或 240 dpi) → `Image:Dpi`
- [ ] TIFF byte order (`II` / `MM`) → `Image:ByteOrder`
- [ ] 是否另外需要灰階/彩色正面影像
- [ ] 影像尺寸、檔案大小上下限 → `Image:Min/Max*Inches`
- [ ] X9 檔案版本 (DSTU X9.37 `03` / X9.100-187 `30`)、必填記錄與欄位 (由後端組檔)
- [ ] 影像品質 (IQA) 要求、重複票檢查是否由 JPM 端處理

> bridge 只負責「每張支票的影像與 MICR」，X9 檔案 (Cash Letter / Bundle / Type 25 / Type 50·52 記錄) 由後端組成，
> 金額通常需要使用者在網頁輸入 (MICR 金額欄位只有已印金額的支票才有)。

## 目錄結構

```
src/CheckScanBridge/
  Program.cs                     HTTP API、CORS、Origin 檢查
  BridgeOptions.cs               設定
  Capture/                       與機型無關：CaptureService、ItemProcessor、MicrParser、API 模型
  Imaging/                       X9TiffCodec (TIFF G4 編解碼/驗證)、重新取樣、黑白化、PNG 預覽
  Drivers/IScannerDriver.cs      驅動介面
  Drivers/DriverFactory.cs       依設定建立驅動 (新增機型在此註冊)
  Drivers/EpsonWebApi/           Epson 網路機種 (Scan Web API) 驅動
  Drivers/Mock/                  模擬驅動
  Drivers/Opos/                  OPOS 通用驅動 + STA 執行緒 (COM 元件用)
  wwwroot/index.html             內建測試頁 http://127.0.0.1:9123/
client/checkscan-bridge.js       瀏覽器端 SDK
client/example-upload.html       掃描 → 上傳後端範例
tests/CheckScanBridge.Tests/     單元/整合測試
scripts/                         建置、安裝/移除 Windows 服務
docs/adding-a-driver.md          新增機型驅動指南
```

## 建置與安裝

```powershell
dotnet test                                   # 執行測試
dotnet run --project src/CheckScanBridge      # Mock 模式，開啟 http://127.0.0.1:9123/
.\scripts\build.ps1                           # 自包含單一執行檔 → .\publish (預設 win-x86)
.\scripts\install-service.ps1                 # (系統管理員) 安裝為 Windows 服務並啟動
.\scripts\uninstall-service.ps1 -RemoveFiles
```

> 廠商 SDK 若只有 32 位元 DLL / COM 元件，須用 `win-x86` 發佈 (預設)；64 位元 SDK 用 `-Runtime win-x64`。
> 若驅動在 Windows 服務 (Session 0) 下無法運作，可改用工作排程器在使用者登入時啟動 `CheckScanBridge.exe`。

設定檔是安裝目錄的 `appsettings.json` (重新安裝會保留)，修改後 `Restart-Service CheckScanBridge`。

```jsonc
"Bridge": {
  "Driver": "EpsonWebApi",                           // EpsonWebApi | Mock | Opos
  "AllowedOrigins": ["https://你的網站"],
  "Requirements": { "RequireBack": true, "RequireMicr": true },
  "Image": { "Dpi": 200, "ByteOrder": "LittleEndian" }
}
```

## HTTP API

| Method | Path | 說明 |
|---|---|---|
| GET  | `/api/health` | `{ status, version, device, unmetRequirements }` |
| GET  | `/api/device` | `{ device, available, busy, message, capabilities, unmetRequirements }` |
| POST | `/api/scan` | 等待放入支票並掃描 `{ timeoutMs?, batch?, maxItems?, preview? }` |
| POST | `/api/scan/cancel` | 取消進行中的掃描 |

`POST /api/scan` 回應：

```jsonc
{
  "scanId": "…", "scannedAt": "…", "device": "Mock",
  "items": [{
    "sequence": 1, "itemId": "…",
    "front": { "side": "front", "contentType": "image/tiff", "format": "x9-tiff-g4",
               "width": 1200, "height": 550, "dpi": 200, "bytes": 856,
               "base64": "…", "previewPngBase64": "…" },
    "back":  { … },
    "micr": { "raw": "T021000021T 1234567890U 0123", "routingNumber": "021000021", "routingValid": true,
              "auxiliaryOnUs": null, "epc": null, "onUs": "1234567890/0123",
              "accountNumber": "1234567890", "serialNumber": "0123", "amountCents": null, "hasUnreadable": false },
    "warnings": []
  }],
  "error": null          // 連續掃描中途出錯時：{ code, message }，items 仍保留已掃描的支票
}
```

`accountNumber` / `serialNumber` 是依常見版面推測的欄位；組 X9 Type 25 時請使用 `onUs`、`auxiliaryOnUs`、`epc`、`routingNumber`。

**警告 (warnings)**：`MICR_MISSING`、`MICR_UNREADABLE_CHARACTERS`、`MICR_ROUTING_MISSING`、`MICR_ROUTING_CHECKSUM_FAILED`、
`FRONT/BACK_IMAGE_SIZE_OUT_OF_RANGE`、`FRONT/BACK_IMAGE_TOO_LIGHT`、`FRONT/BACK_IMAGE_TOO_DARK`、`FRONT_BACK_SIZE_MISMATCH`，
以及驅動回報的警告 (EpsonWebApi：`IQA_IMAGE_TOO_LIGHT`、`IQA_PIGGY_BACK` 等掃描機 IQA 未通過項目、`MICR_PARSE_MISMATCH`)。建議網頁端有警告時要求人工確認或重掃。

**錯誤** `{ "error": { "code", "message" } }`：

| code | HTTP | 說明 |
|---|---|---|
| `CAPABILITY_MISSING` | 501 | 目前驅動不支援必要功能 (例如背面影像) |
| `PAPER_JAM` / `DOUBLE_FEED` / `MISINSERTION` / `PAPER_IN_PATH` / `COVER_OPEN` | 500 / 409 / 503 | 掃描機回報的紙張/機構問題 |
| `BUSY` | 409 | 已有掃描進行中 / 裝置被占用 |
| `CANCELLED` | 409 | 已取消 |
| `TIMEOUT` | 408 | 等待放入支票逾時 |
| `DEVICE_NOT_FOUND` / `DEVICE_OFFLINE` / `DRIVER_NOT_INSTALLED` | 503 | 裝置或驅動問題 |
| `FRONT_IMAGE_MISSING` / `BACK_IMAGE_MISSING` / `IMAGE_PROFILE_ERROR` | 500 | 影像不完整或無法產生符合規格的影像 |
| `ORIGIN_NOT_ALLOWED` / `UNAUTHORIZED` | 403 / 401 | 來源不在白名單 / API Key 錯誤 |

## 網頁端使用

```js
import { CheckScanBridge } from './checkscan-bridge.js';

const bridge = new CheckScanBridge();
const { items, error } = await bridge.scan({ batch: true });
for (const item of items) {
  form.append(`front_${item.sequence}`, CheckScanBridge.toBlob(item.front), 'front.tif'); // X9 TIFF G4
  form.append(`back_${item.sequence}`,  CheckScanBridge.toBlob(item.back),  'back.tif');
  form.append(`micr_${item.sequence}`,  JSON.stringify(item.micr));
  img.src = CheckScanBridge.previewUrl(item.front);  // PNG 預覽
}
```

完整範例見 [`client/example-upload.html`](client/example-upload.html)。

## 安全性與瀏覽器

- 只綁定 `127.0.0.1`；`AllowedHosts` 阻擋 DNS rebinding。
- **Origin 白名單由伺服器端強制檢查** (不只靠 CORS)，其他網站無法觸發掃描；`ApiKey` 為選用的額外防護。
- 影像只回傳給呼叫的網頁，bridge 不儲存、不對外傳送。
- **Chrome / Edge Local Network Access (LNA)**：Chrome 142 起，公開網站第一次呼叫 `127.0.0.1` 時會跳出
  「存取區域網路上的裝置」權限提示，**使用者必須允許**才能連線 (JPM 的掃描元件也受同樣影響)。
  企業環境可用 Chrome/Edge 政策 `LocalNetworkAccessAllowedForUrls` 對你的網站預先允許；
  政策細節在各版本間仍有變動，部署前請以目前 Chrome 版本實測。

## 目前限制

- `EpsonWebApi` 驅動依手冊與模擬掃描機測試，尚未在實機驗證 (MICR 符號、Authorization 格式、輪詢時序需實測)。
- 灰階來源的黑白化為通用演算法；實務上應優先使用掃描機原生黑白輸出 (各廠 SDK 通常提供 X9 用的 G4 輸出)。
- 影像品質檢查只有尺寸與黑色比例的基本檢查，並非完整的 X9.100-180 IQA。
