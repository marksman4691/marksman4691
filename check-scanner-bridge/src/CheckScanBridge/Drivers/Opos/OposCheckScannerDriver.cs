using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using CheckScanBridge.Capture;

namespace CheckScanBridge.Drivers.Opos;

/// <summary>
/// 通用 UnifiedPOS / OPOS CheckScanner 驅動 (跨廠牌，但只有正面影像、沒有 MICR)。
/// 因 UPOS 標準未定義背面影像，在 RequireBack=true 時無法使用；
/// 保留作為參考實作與只需正面影像的情境。各廠牌應改用原生 SDK 驅動。
///
/// 流程：Open → ClaimDevice → DeviceEnabled → BeginInsertion → EndInsertion
///       → RetrieveImage → ImageData → BeginRemoval/EndRemoval → Release/Close
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class OposCheckScannerDriver : IScannerDriver, IDisposable
{
    private const int OPOS_SUCCESS = 0, OPOS_E_TIMEOUT = 112, OPOS_BC_NIBBLE = 1;
    private const int CHK_CROP_AREA_ENTIRE_IMAGE = -1, CHK_IF_TIFF = 2, CHK_CL_MONO = 1;

    private readonly OposOptions _opt;
    private readonly ILogger _log;
    private readonly StaThread _sta = new("OPOS-STA");

    public OposCheckScannerDriver(OposOptions options, ILogger<OposCheckScannerDriver> log)
    {
        _opt = options;
        _log = log;
    }

    public string Name => $"OPOS:{_opt.LogicalName}";

    public DriverCapabilities Capabilities { get; } = new(Front: true, Back: false, Micr: false, Batch: false);

    public Task<DriverStatus> GetStatusAsync(CancellationToken ct) => _sta.InvokeAsync(() =>
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
            return new DriverStatus(true, "裝置就緒");
        }
        catch (ScannerException ex)
        {
            return new DriverStatus(false, ex.Message);
        }
        finally
        {
            Release(chk);
        }
    });

    public async IAsyncEnumerable<RawItem> CaptureAsync(CaptureOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        yield return await _sta.InvokeAsync(() => ScanOnSta(options, ct));
    }

    private RawItem ScanOnSta(CaptureOptions options, CancellationToken ct)
    {
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
            chk.ImageFormat = CHK_IF_TIFF;
            chk.Color = CHK_CL_MONO;
            chk.Quality = options.PreferredDpi;

            WaitForInsertion(chk, options.InsertionTimeout, ct);
            Check(chk, (int)chk.EndInsertion(), "EndInsertion");
            Check(chk, (int)chk.RetrieveImage(CHK_CROP_AREA_ENTIRE_IMAGE), "RetrieveImage");
            string data = chk.ImageData;
            var bytes = DecodeNibble(data);
            if (bytes.Length == 0) throw new ScannerException("NO_IMAGE", "掃描機未回傳影像資料");

            int rc = chk.BeginRemoval(_opt.RemovalTimeoutMs);
            if (rc == OPOS_SUCCESS) chk.EndRemoval();
            else _log.LogWarning("BeginRemoval returned {Rc}", rc);

            return new RawItem(RawImage.FromTiff(bytes, options.PreferredDpi), null, null);
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
            int rc = chk.BeginInsertion((int)Math.Min(_opt.InsertionPollMs, Math.Max(1, remaining.TotalMilliseconds)));
            if (rc == OPOS_SUCCESS) return;
            if (rc != OPOS_E_TIMEOUT) Check(chk, rc, "BeginInsertion");
            StaThread.PumpMessages();
        }
    }

    private dynamic CreateControl()
    {
        var type = Type.GetTypeFromProgID(_opt.CheckScannerProgId)
            ?? throw new ScannerException("DRIVER_NOT_INSTALLED",
                $"找不到 OPOS 控制項 '{_opt.CheckScannerProgId}' (注意 32/64 位元需與本程式一致)",
                StatusCodes.Status503ServiceUnavailable);
        return Activator.CreateInstance(type)
            ?? throw new ScannerException("DRIVER_NOT_INSTALLED", "無法建立 OPOS 控制項", StatusCodes.Status503ServiceUnavailable);
    }

    private static void Check(dynamic chk, int rc, string op)
    {
        if (rc == OPOS_SUCCESS) return;
        int ext = 0;
        try { ext = chk.ResultCodeExtended; } catch { /* ignore */ }
        var (code, status) = rc switch
        {
            104 or 109 => ("DEVICE_NOT_FOUND", StatusCodes.Status503ServiceUnavailable),
            102 or 113 => ("BUSY", StatusCodes.Status409Conflict),
            107 or 108 => ("DEVICE_OFFLINE", StatusCodes.Status503ServiceUnavailable),
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
        if (com is not null && Marshal.IsComObject(com)) Marshal.FinalReleaseComObject(com);
    }

    public void Dispose() => _sta.Dispose();
}
