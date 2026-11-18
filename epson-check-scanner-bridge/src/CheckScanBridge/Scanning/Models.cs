namespace CheckScanBridge.Scanning;

public enum ImageFormat { Jpeg, Tiff, Bmp }

public enum ColorMode { Mono, Gray, Color }

public sealed class ScanRequest
{
    /// <summary>等待使用者放入支票的最長時間。</summary>
    public int? TimeoutMs { get; set; }
    public ImageFormat? Format { get; set; }
    public ColorMode? Color { get; set; }
    public int? Dpi { get; set; }
}

public sealed record ScanImage(string Side, string ContentType, int Bytes, string Base64);

public sealed record ScanResult(
    string ScanId,
    DateTimeOffset ScannedAt,
    string Device,
    IReadOnlyList<ScanImage> Images,
    string? Micr);

public sealed record DeviceStatus(string Device, bool Available, bool Busy, string Message);

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

public static class ImageFormats
{
    public static string ContentType(ImageFormat f) => f switch
    {
        ImageFormat.Jpeg => "image/jpeg",
        ImageFormat.Tiff => "image/tiff",
        ImageFormat.Bmp => "image/bmp",
        _ => "application/octet-stream",
    };
}
