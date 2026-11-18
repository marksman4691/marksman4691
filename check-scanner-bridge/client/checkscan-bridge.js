/**
 * CheckScanBridge 瀏覽器端 SDK
 * 讓網頁透過本機 bridge (預設 http://127.0.0.1:9123) 啟動 Epson 票據掃描機。
 *
 *   import { CheckScanBridge } from './checkscan-bridge.js';
 *   const bridge = new CheckScanBridge();
 *   const result = await bridge.scan({ batch: false });
 *   for (const item of result.items) {
 *     const front = CheckScanBridge.toBlob(item.front);   // X9 TIFF G4 (上傳用)
 *     img.src = CheckScanBridge.previewUrl(item.front);   // PNG 預覽 (顯示用)
 *   }
 */
export class CheckScanBridgeError extends Error {
  constructor(code, message, status) {
    super(message);
    this.name = 'CheckScanBridgeError';
    this.code = code;     // BRIDGE_UNREACHABLE | BUSY | TIMEOUT | CANCELLED | DEVICE_NOT_FOUND | ...
    this.status = status; // HTTP status (未連線時為 0)
  }
}

export class CheckScanBridge {
  /**
   * @param {{ baseUrl?: string, apiKey?: string }} [options]
   */
  constructor({ baseUrl = 'http://127.0.0.1:9123', apiKey } = {}) {
    this.baseUrl = baseUrl.replace(/\/+$/, '');
    this.apiKey = apiKey;
  }

  /** bridge 是否在執行 (未安裝/未啟動時 reject BRIDGE_UNREACHABLE)。 */
  health() {
    return this.#request('GET', '/api/health');
  }

  /** 掃描機狀態：{ device, available, busy, message, capabilities, unmetRequirements } */
  device() {
    return this.#request('GET', '/api/device');
  }

  /**
   * 啟動掃描：resolve 時代表支票已放入並掃描完成。
   * 連續掃描中途出錯時仍會 resolve，已掃描的支票在 items，錯誤在 result.error。
   * @param {{ timeoutMs?: number, batch?: boolean, maxItems?: number, preview?: boolean }} [options]
   * @param {{ signal?: AbortSignal }} [init]
   * @returns {Promise<{ scanId: string, scannedAt: string, device: string,
   *   items: CheckItem[], error: { code: string, message: string } | null }>}
   *
   * @typedef {{ side: string, contentType: string, format: string, width: number, height: number,
   *   dpi: number, bytes: number, base64: string, previewPngBase64: string|null }} CheckImage
   * @typedef {{ raw: string, routingNumber: string|null, routingValid: boolean, auxiliaryOnUs: string|null,
   *   epc: string|null, onUs: string|null, accountNumber: string|null, serialNumber: string|null,
   *   amountCents: number|null, hasUnreadable: boolean }} MicrData
   * @typedef {{ sequence: number, itemId: string, front: CheckImage, back: CheckImage,
   *   micr: MicrData|null, warnings: string[] }} CheckItem
   */
  scan(options = {}, { signal } = {}) {
    return this.#request('POST', '/api/scan', options, signal);
  }

  /** 取消進行中的掃描 (例如使用者按下「取消」)。 */
  cancel() {
    return this.#request('POST', '/api/scan/cancel', {});
  }

  /** 將回傳的影像轉成 Blob，可直接放進 FormData 上傳。 */
  static toBlob(image) {
    const bin = atob(image.base64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return new Blob([bytes], { type: image.contentType });
  }

  /** PNG 預覽圖的 data URL，可直接設定到 <img src> (TIFF 在多數瀏覽器無法顯示)。 */
  static previewUrl(image) {
    return image.previewPngBase64 ? `data:image/png;base64,${image.previewPngBase64}` : null;
  }

  async #request(method, path, body, signal) {
    const headers = {};
    if (body !== undefined) headers['Content-Type'] = 'application/json';
    if (this.apiKey) headers['X-Bridge-Key'] = this.apiKey;

    let res;
    try {
      res = await fetch(this.baseUrl + path, {
        method,
        headers,
        body: body !== undefined ? JSON.stringify(body) : undefined,
        signal,
      });
    } catch (e) {
      if (e?.name === 'AbortError') throw new CheckScanBridgeError('CANCELLED', '已取消', 0);
      throw new CheckScanBridgeError('BRIDGE_UNREACHABLE',
        '無法連線到掃描機 Bridge，請確認程式已安裝並啟動', 0);
    }

    const data = await res.json().catch(() => null);
    if (!res.ok) {
      const err = data?.error ?? {};
      throw new CheckScanBridgeError(err.code ?? 'HTTP_' + res.status, err.message ?? res.statusText, res.status);
    }
    return data;
  }
}

if (typeof window !== 'undefined') window.CheckScanBridge = CheckScanBridge;
