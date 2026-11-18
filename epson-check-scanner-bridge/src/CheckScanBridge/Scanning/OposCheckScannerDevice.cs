using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CheckScanBridge.Scanning;

/// <summary>
/// 透過 UnifiedPOS / OPOS 標準的 CheckScanner 控制項操作 Epson TM-S 系列票據掃描機。
/// 前置需求：安裝 Epson TM-S 驅動程式 + EPSON OPOS ADK，並用 SetupPOS 建立 CheckScanner 邏輯裝置。
///
/// 掃描流程 (UPOS CheckScanner)：
///   Open → ClaimDevice → DeviceEnabled=true → 設定影像參數
///   → BeginInsertion (等待放入支票) → EndInsertion (進紙/掃描)
///   → RetrieveImage → 讀取 ImageData → BeginRemoval/EndRemoval (退紙)
///   → DeviceEnabled=false → ReleaseDevice → Close
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OposCheckScannerDevice : IScannerDevice, IDisposable
{
    // OPOS 結果碼 (Opos.h)
    private const int OPOS_SUCCESS = 0;
    private const int OPOS_E_TIMEOUT = 112;

    // OPOS BinaryConversion (Opos.h)
    private const int OPOS_BC_NIBBLE = 1;

    // CheckScanner 常數 (OposChk.h)
    private const int CHK_CROP_AREA_ENTIRE_IMAGE = -1;
    private const int CHK_IF_TIFF = 2, CHK_IF_BMP = 3, CHK_IF_JPEG = 4;
    private const int CHK_CL_MONO = 1, CHK_CL_GRAYSCALE = 2, CHK_CL_FULL = 5;

    private readonly OposOptions _opt;
    private readonly ILogger _log;
    private readonly StaThread _sta = new("OPOS-STA");

    public OposCheckScannerDevice(OposOptions options, ILogger<OposCheckScannerDevice> log)
    {
        _opt = options;
        _log = log;
    }

    public string Name => $"OPOS:{_opt.LogicalName}";

    public Task<DeviceStatus> GetStatusAsync(CancellationToken ct) => _sta.InvokeAsync(() =>
    {
        dynamic? chk = null;
        try
        {
            chk = CreateControl();
            Check(chk, (int)chk.Open(_opt.LogicalName), "Open");
            try
            {
                Check(chk, (int)chk.ClaimDevice(_opt.ClaimTimeoutMs), "ClaimDevice");
                chk.ReleaseDevice();
            }
            finally
            {
                chk.Close();
            }
            return new DeviceStatus(Name, true, false, "裝置就緒");
        }
        catch (ScannerException ex)
        {
            return new DeviceStatus(Name, false, false, ex.Message);
        }
        finally
        {
            Release(chk);
        }
    });

    public Task<ScanResult> ScanAsync(ScanRequest request, TimeSpan insertionTimeout, CancellationToken ct) =>
        _sta.InvokeAsync(() => ScanOnSta(request, insertionTimeout, ct));

    private ScanResult ScanOnSta(ScanRequest request, TimeSpan insertionTimeout, CancellationToken ct)
    {
        var format = request.Format ?? _opt.DefaultFormat;
        var color = request.Color ?? _opt.DefaultColor;
        var dpi = request.Dpi ?? _opt.DefaultDpi;

        dynamic? chk = null;
        bool opened = false, claimed = false, enabled = false;
        try
        {
            ct.ThrowIfCancellationRequested();
            chk = CreateControl();

            Check(chk, (int)chk.Open(_opt.LogicalName), "Open");
            opened = true;
            Check(chk, (int)chk.ClaimDevice(_opt.ClaimTimeoutMs), "ClaimDevice");
            claimed = true;
            chk.DeviceEnabled = true;
            Check(chk, (int)chk.ResultCode, "DeviceEnabled");
            enabled = true;

            chk.BinaryConversion = OPOS_BC_NIBBLE;
            chk.ImageFormat = format switch
            {
                ImageFormat.Tiff => CHK_IF_TIFF,
                ImageFormat.Bmp => CHK_IF_BMP,
                _ => CHK_IF_JPEG,
            };
            Check(chk, (int)chk.ResultCode, "ImageFormat");
            chk.Color = color switch
            {
                ColorMode.Mono => CHK_CL_MONO,
                ColorMode.Color => CHK_CL_FULL,
                _ => CHK_CL_GRAYSCALE,
            };
            Check(chk, (int)chk.ResultCode, "Color");
            if (dpi > 0)
            {
                chk.Quality = dpi;
                Check(chk, (int)chk.ResultCode, "Quality");
            }

            WaitForInsertion(chk, insertionTimeout, ct);

            Check(chk, (int)chk.EndInsertion(), "EndInsertion");
            Check(chk, (int)chk.RetrieveImage(CHK_CROP_AREA_ENTIRE_IMAGE), "RetrieveImage");
            string imageData = chk.ImageData;
            var bytes = DecodeNibble(imageData);
            if (bytes.Length == 0)
                throw new ScannerException("NO_IMAGE", "掃描機未回傳影像資料");

            EjectCheck(chk);

            var images = new List<ScanImage>
            {
                new("front", ImageFormats.ContentType(format), bytes.Length, Convert.ToBase64String(bytes)),
            };
            // TODO: 背面影像與 MICR 需使用 Epson OPOS 的 DirectIO 或 MICR 控制項，
            //       依實際機型 (TM-S1000 / TM-S2000 / TM-S9000) 的 OPOS ADK 手冊實作。
            return new ScanResult(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, Name, images, null);
        }
        finally
        {
            if (chk is not null)
            {
                if (enabled) Try(() => chk.DeviceEnabled = false);
                if (claimed) Try(() => chk.ReleaseDevice());
                if (opened) Try(() => chk.Close());
            }
            Release(chk);
        }
    }

    /// <summary>分段呼叫 BeginInsertion，讓取消與總逾時都能生效。</summary>
    private void WaitForInsertion(dynamic chk, TimeSpan timeout, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var remaining = timeout - sw.Elapsed;
            if (remaining <= TimeSpan.Zero)
                throw new ScannerException("TIMEOUT", "等待放入支票逾時", StatusCodes.Status408RequestTimeout);

            var wait = (int)Math.Min(_opt.InsertionPollMs, Math.Max(1, remaining.TotalMilliseconds));
            int rc = chk.BeginInsertion(wait);
            if (rc == OPOS_SUCCESS) return;
            if (rc != OPOS_E_TIMEOUT) Check(chk, rc, "BeginInsertion");
            StaThread.PumpMessages();
        }
    }

    private void EjectCheck(dynamic chk)
    {
        int rc = chk.BeginRemoval(_opt.RemovalTimeoutMs);
        if (rc == OPOS_SUCCESS)
        {
            rc = chk.EndRemoval();
            if (rc != OPOS_SUCCESS) _log.LogWarning("EndRemoval returned {Rc}", rc);
        }
        else
        {
            _log.LogWarning("BeginRemoval returned {Rc}", rc);
        }
    }

    private dynamic CreateControl()
    {
        var type = Type.GetTypeFromProgID(_opt.CheckScannerProgId)
            ?? throw new ScannerException("OPOS_NOT_INSTALLED",
                $"找不到 OPOS 控制項 '{_opt.CheckScannerProgId}'，請安裝 EPSON OPOS ADK (注意 32/64 位元需與本程式一致)",
                StatusCodes.Status503ServiceUnavailable);
        return Activator.CreateInstance(type)
            ?? throw new ScannerException("OPOS_NOT_INSTALLED", "無法建立 OPOS 控制項", StatusCodes.Status503ServiceUnavailable);
    }

    private static void Check(dynamic chk, int rc, string op)
    {
        if (rc == OPOS_SUCCESS) return;
        int ext = 0;
        try { ext = chk.ResultCodeExtended; } catch { /* ignore */ }
        var (code, status) = rc switch
        {
            104 or 109 => ("DEVICE_NOT_FOUND", StatusCodes.Status503ServiceUnavailable), // NOSERVICE / NOEXIST
            102 or 113 => ("BUSY", StatusCodes.Status409Conflict),                       // CLAIMED / BUSY
            107 or 108 => ("DEVICE_OFFLINE", StatusCodes.Status503ServiceUnavailable),   // NOHARDWARE / OFFLINE
            112 => ("TIMEOUT", StatusCodes.Status408RequestTimeout),
            _ => ("DEVICE_ERROR", StatusCodes.Status500InternalServerError),
        };
        throw new ScannerException(code, $"OPOS {op} 失敗 (ResultCode={rc}, Extended={ext})", status);
    }

    /// <summary>BinaryConversion=Nibble：每個 byte 以兩個字元 (0x30 + 高/低 4 bits) 表示。</summary>
    internal static byte[] DecodeNibble(string? s)
    {
        if (string.IsNullOrEmpty(s)) return Array.Empty<byte>();
        var result = new byte[s.Length / 2];
        for (var i = 0; i < result.Length; i++)
            result[i] = (byte)(((s[2 * i] - 0x30) << 4) | ((s[2 * i + 1] - 0x30) & 0x0F));
        return result;
    }

    private static void Try(Action a)
    {
        try { a(); } catch { /* 清理階段忽略錯誤 */ }
    }

    private static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
            Marshal.FinalReleaseComObject(com);
    }

    public void Dispose() => _sta.Dispose();
}
