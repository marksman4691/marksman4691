using CheckScanBridge.Imaging;

namespace CheckScanBridge.Drivers;

/// <summary>
/// 掃描機驅動介面 (每個廠牌/SDK 一個實作)。
/// 驅動只負責「取得原始資料」，影像格式轉換、MICR 解析、品質檢查都由共用的 Capture 管線處理，
/// 因此新增機型時只要實作這個介面即可。詳見 docs/adding-a-driver.md。
/// </summary>
public interface IScannerDriver
{
    /// <summary>顯示名稱，例如 "Epson TM-S1000"。</summary>
    string Name { get; }

    DriverCapabilities Capabilities { get; }

    Task<DriverStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>
    /// 等待放入支票並逐張回傳。單張模式回傳一張後結束；
    /// 連續模式 (options.Batch) 持續送紙直到進紙槽空、達到 MaxItems 或取消。
    /// 第一張在 InsertionTimeout 內未放入時丟出 TIMEOUT 的 ScannerException。
    /// </summary>
    IAsyncEnumerable<RawItem> CaptureAsync(CaptureOptions options, CancellationToken ct);
}

public sealed record DriverCapabilities(bool Front, bool Back, bool Micr, bool Batch);

public sealed record DriverStatus(bool Available, string Message);

public sealed class CaptureOptions
{
    public TimeSpan InsertionTimeout { get; init; }
    public bool Batch { get; init; }
    public int MaxItems { get; init; } = 1;
    /// <summary>建議的掃描解析度；驅動無法設定時可回傳其他 DPI，管線會重新取樣。</summary>
    public int PreferredDpi { get; init; } = 200;
}

/// <summary>驅動回傳的原始影像：TIFF bytes (任何壓縮/色彩) 或灰階像素其一。</summary>
public sealed class RawImage
{
    private RawImage(byte[]? tiff, GrayImage? gray, int dpi)
    {
        Tiff = tiff;
        Gray = gray;
        Dpi = dpi;
    }

    public byte[]? Tiff { get; }
    public GrayImage? Gray { get; }
    /// <summary>TIFF 未記錄解析度時使用的 DPI。</summary>
    public int Dpi { get; }

    public static RawImage FromTiff(byte[] data, int dpiHint) => new(data, null, dpiHint);
    public static RawImage FromGray(GrayImage image) => new(null, image, image.Dpi);
}

/// <summary>
/// 一張支票的原始掃描結果。
/// MicrRaw 須轉為標準字元：0-9、T=Transit⑆、U=On-Us⑈、A=Amount⑇、D=Dash⑉、?=無法辨識、空白。
/// </summary>
public sealed record RawItem(RawImage? Front, RawImage? Back, string? MicrRaw, IReadOnlyList<string>? DriverWarnings = null);
