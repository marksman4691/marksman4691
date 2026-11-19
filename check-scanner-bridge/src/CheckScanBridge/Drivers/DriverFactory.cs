using CheckScanBridge.Drivers.EpsonWebApi;
using CheckScanBridge.Drivers.Mock;
using CheckScanBridge.Drivers.Opos;

namespace CheckScanBridge.Drivers;

/// <summary>依設定的 Driver 名稱建立驅動。新增機型時在這裡註冊。</summary>
public static class DriverFactory
{
    public static readonly string[] Known = { "Mock", "EpsonWebApi", "Opos" };

    public static IScannerDriver Create(BridgeOptions options, IServiceProvider sp)
    {
        switch (options.Driver.ToLowerInvariant())
        {
            case "mock":
                return new MockScannerDriver(options.Mock);
            case "epsonwebapi":
                return new EpsonWebApiDriver(options.EpsonWebApi, sp.GetRequiredService<ILogger<EpsonWebApiDriver>>());
            case "opos":
                if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("OPOS 只能在 Windows 上執行");
                return new OposCheckScannerDriver(options.Opos, sp.GetRequiredService<ILogger<OposCheckScannerDriver>>());
            // case "epsonusb":     return new EpsonTmsUsbDriver(...);    // Epson TM-S USB 機種 (驅動 API)
            // case "panini":       return new PaniniVisionDriver(...);   // Panini Vision API
            // case "digitalcheck": return new DigitalCheckDriver(...);   // Digital Check SDK
            default:
                throw new InvalidOperationException(
                    $"未知的 Driver '{options.Driver}'，可用：{string.Join(", ", Known)}");
        }
    }
}
