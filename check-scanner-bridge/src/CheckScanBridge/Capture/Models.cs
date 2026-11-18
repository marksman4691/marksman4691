using CheckScanBridge.Drivers;

namespace CheckScanBridge.Capture;

public sealed class ScanRequest
{
    /// <summary>等待放入第一張支票的最長時間。</summary>
    public int? TimeoutMs { get; set; }
    /// <summary>連續掃描 (需驅動支援，否則只掃一張)。</summary>
    public bool Batch { get; set; }
    public int? MaxItems { get; set; }
    /// <summary>是否附上 PNG 預覽圖 (TIFF G4 在多數瀏覽器無法顯示)。</summary>
    public bool Preview { get; set; } = true;
}

public sealed record CheckImage(
    string Side,
    string ContentType,
    string Format,
    int Width,
    int Height,
    int Dpi,
    int Bytes,
    string Base64,
    string? PreviewPngBase64);

/// <summary>MICR 解析結果；X9 欄位可直接用於 Type 25 Check Detail Record。</summary>
public sealed record MicrData(
    string Raw,
    string? RoutingNumber,
    bool RoutingValid,
    string? AuxiliaryOnUs,
    string? Epc,
    string? OnUs,
    string? AccountNumber,
    string? SerialNumber,
    long? AmountCents,
    bool HasUnreadable);

public sealed record CheckItem(
    int Sequence,
    string ItemId,
    CheckImage Front,
    CheckImage Back,
    MicrData? Micr,
    IReadOnlyList<string> Warnings);

public sealed record ScanError(string Code, string Message);

public sealed record ScanResult(
    string ScanId,
    DateTimeOffset ScannedAt,
    string Device,
    IReadOnlyList<CheckItem> Items,
    /// <summary>連續掃描中途出錯時，已掃描的支票仍會回傳，並在此說明錯誤。</summary>
    ScanError? Error);

public sealed record DeviceStatus(
    string Device,
    bool Available,
    bool Busy,
    string Message,
    DriverCapabilities Capabilities,
    IReadOnlyList<string> UnmetRequirements);

public sealed class ScannerException : Exception
{
    public ScannerException(string code, string message, int statusCode = 500, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        StatusCode = statusCode;
    }

    public string Code { get; }
    public int StatusCode { get; }
}
