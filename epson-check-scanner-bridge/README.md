# Epson Check Scanner Bridge

在 Windows 上執行的本機小型服務，作為 **網頁 (瀏覽器)** 與 **Epson 票據/支票掃描機 (TM-S 系列)** 之間的橋樑，
讓網頁程式可以啟動掃描機、取得支票影像，再上傳到自己的後端。

```
┌──────────────┐  fetch http://127.0.0.1:9123   ┌───────────────────┐   OPOS (COM)   ┌──────────────┐
│ 網頁 (瀏覽器) │ ─────────────────────────────▶ │ CheckScanBridge   │ ─────────────▶ │ Epson TM-S   │
│ checkscan-    │ ◀───── JSON + base64 影像 ──── │ (Windows 服務)    │ ◀───────────── │ 票據掃描機   │
│ bridge.js     │                                └───────────────────┘                └──────────────┘
└──────┬───────┘
       │ FormData 上傳
       ▼
  你的後端 API
```

- 技術：C# / .NET 8 (ASP.NET Core minimal API)，可當 Windows 服務或一般程式執行
- 掃描機介面：UnifiedPOS **OPOS CheckScanner** 控制項 (Epson OPOS ADK 提供)
- 只監聽 `127.0.0.1`，並以 Origin 白名單 + 選用 API Key 保護
- 內建 **Mock 模擬裝置**，沒有實機也能開發網頁端

## 目錄結構

```
src/CheckScanBridge/          .NET 8 服務程式
  Program.cs                  HTTP API、CORS/Origin/PNA 安全設定
  Scanning/OposCheckScannerDevice.cs   Epson OPOS 掃描實作
  Scanning/MockScannerDevice.cs        模擬裝置
  Scanning/StaThread.cs                OPOS (ActiveX) 專用 STA 執行緒
  wwwroot/index.html          內建測試頁 (http://127.0.0.1:9123/)
client/checkscan-bridge.js    瀏覽器端 SDK (ES module)
client/example-upload.html    掃描後上傳到後端的整合範例
scripts/                      build / 安裝 / 移除 Windows 服務
```

## 掃描機端前置作業 (實機)

1. 安裝 Epson TM-S 系列驅動程式 (TM-S1000 / TM-S2000 / TM-S9000 等，依機型)。
2. 安裝 **EPSON OPOS ADK**。
3. 用 **SetupPOS** 新增 *CheckScanner* 裝置，記下「邏輯裝置名稱 (LDN)」，並用其中的 Check Health 測試掃描。
4. 修改 `appsettings.json`：

```jsonc
"Bridge": {
  "Device": "Opos",                          // Mock = 模擬, Opos = 實機
  "AllowedOrigins": ["https://你的網站"],     // 允許呼叫的網頁來源
  "Opos": { "LogicalName": "你在 SetupPOS 設定的名稱" }
}
```

> **位元數**：OPOS ActiveX 控制項多為 32 位元，因此預設發佈 `win-x86`。若安裝的是 64 位元 OPOS ADK，請改用 `-Runtime win-x64`。

## 建置與安裝

開發機 (需 .NET 8 SDK)：

```powershell
# 直接執行 (預設 Mock 模式)，開啟 http://127.0.0.1:9123/ 測試
dotnet run --project src/CheckScanBridge

# 產生自包含單一執行檔到 .\publish (目標電腦不需安裝 .NET)
.\scripts\build.ps1                 # 或 -Runtime win-x64
```

目標電腦 (以系統管理員 PowerShell)：

```powershell
.\scripts\install-service.ps1       # 安裝到 C:\Program Files\CheckScanBridge 並啟動服務
.\scripts\uninstall-service.ps1 -RemoveFiles
```

設定檔位於安裝目錄的 `appsettings.json`，修改後重新啟動服務 (`Restart-Service CheckScanBridge`)。
重新安裝時會保留既有的 `appsettings.json`。

> 若某些驅動在 Windows 服務 (Session 0) 下無法運作，可改為使用者登入時以「工作排程器」啟動 `CheckScanBridge.exe`。

## HTTP API

| Method | Path | 說明 |
|---|---|---|
| GET  | `/api/health` | bridge 是否執行中 `{ status, version, device }` |
| GET  | `/api/device` | 掃描機狀態 `{ device, available, busy, message }` |
| POST | `/api/scan` | 等待放入支票並掃描 (請求會一直等到掃描完成/逾時) |
| POST | `/api/scan/cancel` | 取消進行中的掃描 |

`POST /api/scan` 請求 (全部選填)：

```json
{ "timeoutMs": 30000, "format": "Jpeg", "color": "Gray", "dpi": 200 }
```

- `format`: `Jpeg` | `Tiff` | `Bmp`　`color`: `Mono` | `Gray` | `Color`

回應：

```json
{
  "scanId": "…",
  "scannedAt": "2026-11-18T10:12:17+08:00",
  "device": "OPOS:CheckScanner",
  "images": [ { "side": "front", "contentType": "image/jpeg", "bytes": 48213, "base64": "…" } ],
  "micr": null
}
```

錯誤格式 `{ "error": { "code", "message" } }`：

| code | HTTP | 說明 |
|---|---|---|
| `BUSY` | 409 | 已有掃描進行中 / 裝置被其他程式占用 |
| `CANCELLED` | 409 | 已取消 (呼叫 cancel 或瀏覽器中斷請求) |
| `TIMEOUT` | 408 | 等待放入支票逾時 |
| `DEVICE_NOT_FOUND` / `DEVICE_OFFLINE` / `OPOS_NOT_INSTALLED` | 503 | 裝置或驅動問題 |
| `ORIGIN_NOT_ALLOWED` / `UNAUTHORIZED` | 403 / 401 | 來源不在白名單 / API Key 錯誤 |
| `DEVICE_ERROR` | 500 | 其他 OPOS 錯誤 (訊息含 ResultCode / Extended) |

## 網頁端使用

```js
import { CheckScanBridge } from './checkscan-bridge.js';

const bridge = new CheckScanBridge(); // { baseUrl: 'http://127.0.0.1:9123', apiKey }
await bridge.health();                // 未安裝/未啟動 → CheckScanBridgeError code=BRIDGE_UNREACHABLE

const result = await bridge.scan({ format: 'Jpeg', color: 'Gray', dpi: 200 });

const form = new FormData();
for (const img of result.images)
  form.append(img.side, CheckScanBridge.toBlob(img), `${result.scanId}_${img.side}.jpg`);
await fetch('/api/checks/upload', { method: 'POST', body: form });
```

完整範例見 [`client/example-upload.html`](client/example-upload.html)。

## 安全性設計

- 只綁定 `127.0.0.1`，區網其他電腦無法連線；`AllowedHosts` 阻擋 DNS rebinding。
- **Origin 白名單由伺服器端強制檢查**（不只靠 CORS），其他網站無法觸發掃描。
- 支援 Chrome **Private Network Access**：公開 HTTPS 網站呼叫 `http://127.0.0.1` 時的 preflight 會回應 `Access-Control-Allow-Private-Network: true`。
- `ApiKey` 為選用的額外防護 (會寫在網頁 JS 中，主要防護仍是 Origin 白名單)。
- 影像只回傳給呼叫的網頁，bridge 本身不儲存、不對外上傳。

## 目前限制 / 待實機驗證

- OPOS 流程依 UnifiedPOS CheckScanner 標準實作，**需在實機上驗證** (各機型 Service Object 行為可能略有不同)。
- **背面影像與 MICR 碼**：UPOS CheckScanner 標準只定義單一影像；Epson TM-S 的背面影像、MICR 讀取需透過 Epson OPOS 的 `DirectIO` 或 MICR 控制項，請依機型的 OPOS ADK 手冊在
  `OposCheckScannerDevice.cs` 的 TODO 處補上。Mock 模式已回傳 front/back 與 MICR，網頁端可先按此格式開發。
- TIFF 影像多數瀏覽器無法直接預覽 (上傳不受影響)。
