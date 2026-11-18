/**
 * CheckScanBridge 瀏覽器端 SDK
 * 讓網頁透過本機 bridge (預設 http://127.0.0.1:9123) 啟動 Epson 票據掃描機。
 *
 *   import { CheckScanBridge } from './checkscan-bridge.js';
 *   const bridge = new CheckScanBridge();
 *   const result = await bridge.scan({ format: 'Jpeg', color: 'Gray', dpi: 200 });
 *   const blob = CheckScanBridge.toBlob(result.images[0]);
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

  /** 掃描機狀態：{ device, available, busy, message } */
  device() {
    return this.#request('GET', '/api/device');
  }

  /**
   * 啟動掃描：resolve 時代表使用者已放入支票且掃描完成。
   * @param {{ timeoutMs?: number, format?: 'Jpeg'|'Tiff'|'Bmp', color?: 'Mono'|'Gray'|'Color', dpi?: number }} [options]
   * @param {{ signal?: AbortSignal }} [init]
   * @returns {Promise<{ scanId: string, scannedAt: string, device: string,
   *   images: { side: string, contentType: string, bytes: number, base64: string }[], micr: string|null }>}
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

  /** 將影像轉成 data URL，可直接設定到 <img src>。(TIFF 多數瀏覽器無法顯示) */
  static toDataUrl(image) {
    return `data:${image.contentType};base64,${image.base64}`;
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
