using CheckScanBridge.Scanning;

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

    /// <summary>"Opos" = 真實 Epson 掃描機；"Mock" = 模擬裝置 (開發測試用)。</summary>
    public string Device { get; set; } = "Mock";

    public int DefaultScanTimeoutMs { get; set; } = 30_000;
    public int MaxScanTimeoutMs { get; set; } = 120_000;

    public OposOptions Opos { get; set; } = new();
    public MockOptions Mock { get; set; } = new();
}

public sealed class OposOptions
{
    /// <summary>OPOS CheckScanner Control Object 的 ProgID (Epson OPOS ADK 安裝後註冊)。</summary>
    public string CheckScannerProgId { get; set; } = "OPOS.CheckScanner";

    /// <summary>在 Epson SetupPOS 中設定的 CheckScanner 邏輯裝置名稱 (LDN)。</summary>
    public string LogicalName { get; set; } = "CheckScanner";

    public int ClaimTimeoutMs { get; set; } = 3000;

    /// <summary>每次 BeginInsertion 等待的毫秒數；分段等待才能支援取消。</summary>
    public int InsertionPollMs { get; set; } = 1000;

    public int RemovalTimeoutMs { get; set; } = 5000;

    public ImageFormat DefaultFormat { get; set; } = ImageFormat.Jpeg;
    public ColorMode DefaultColor { get; set; } = ColorMode.Gray;
    public int DefaultDpi { get; set; } = 200;
}

public sealed class MockOptions
{
    public int InsertionDelayMs { get; set; } = 1500;
}
