using CheckScanBridge.Drivers.EpsonWebApi;
using CheckScanBridge.Imaging;

namespace CheckScanBridge;

public sealed class BridgeOptions
{
    /// <summary>Kestrel 監聽位址，請保持 127.0.0.1 以免被區網其他電腦呼叫。</summary>
    public string Url { get; set; } = "http://127.0.0.1:9123";

    /// <summary>允許呼叫 bridge 的網頁來源 (scheme://host[:port])。</summary>
    public List<string> AllowedOrigins { get; set; } = new();

    /// <summary>是否允許沒有 Origin header 的請求 (例如 curl、本機工具)。</summary>
    public bool AllowRequestsWithoutOrigin { get; set; } = true;

    /// <summary>選填：若設定，所有 /api 請求須帶 X-Bridge-Key header。</summary>
    public string? ApiKey { get; set; }

    /// <summary>使用的掃描機驅動：Mock | EpsonWebApi | Opos (之後可新增 Panini、DigitalCheck…)。</summary>
    public string Driver { get; set; } = "Mock";

    public int DefaultScanTimeoutMs { get; set; } = 30_000;
    public int MaxScanTimeoutMs { get; set; } = 120_000;
    public int MaxBatchItems { get; set; } = 100;

    public RequirementOptions Requirements { get; set; } = new();
    public ImageProfileOptions Image { get; set; } = new();
    public EpsonWebApiOptions EpsonWebApi { get; set; } = new();
    public OposOptions Opos { get; set; } = new();
    public MockOptions Mock { get; set; } = new();
}

public sealed class RequirementOptions
{
    /// <summary>每張支票必須有背面影像 (X9 影像交換需正反面)。</summary>
    public bool RequireBack { get; set; } = true;

    /// <summary>驅動必須能讀 MICR；讀不到時該張支票會帶 MICR_MISSING 警告。</summary>
    public bool RequireMicr { get; set; } = true;
}

/// <summary>輸出影像規格。預設值為 X9.37 / X9.100-187 常見要求，請依 JPM H2H 規格書確認。</summary>
public sealed class ImageProfileOptions
{
    public int Dpi { get; set; } = 200;
    public TiffByteOrder ByteOrder { get; set; } = TiffByteOrder.LittleEndian;

    // 基本影像品質檢查 (超出範圍只產生警告)
    public double MinWidthInches { get; set; } = 4.5;
    public double MaxWidthInches { get; set; } = 9.5;
    public double MinHeightInches { get; set; } = 2.0;
    public double MaxHeightInches { get; set; } = 4.5;
    public double MinBlackRatio { get; set; } = 0.005;
    public double MaxBlackRatio { get; set; } = 0.25;

    public int PreviewMaxWidth { get; set; } = 800;
}

public sealed class OposOptions
{
    public string CheckScannerProgId { get; set; } = "OPOS.CheckScanner";
    public string LogicalName { get; set; } = "CheckScanner";
    public int ClaimTimeoutMs { get; set; } = 3000;
    public int InsertionPollMs { get; set; } = 1000;
    public int RemovalTimeoutMs { get; set; } = 5000;
}

public sealed class MockOptions
{
    public int InsertionDelayMs { get; set; } = 1500;
    public int PerItemDelayMs { get; set; } = 400;
    /// <summary>連續掃描時進紙槽中的模擬支票張數。</summary>
    public int ItemsInHopper { get; set; } = 3;
    /// <summary>模擬掃描機原生解析度 (≠200 可測試重新取樣)。</summary>
    public int Dpi { get; set; } = 300;
}
