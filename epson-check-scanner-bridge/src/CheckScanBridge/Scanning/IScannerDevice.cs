namespace CheckScanBridge.Scanning;

public interface IScannerDevice
{
    string Name { get; }

    /// <summary>檢查裝置是否可用 (會嘗試開啟/釋放裝置)。</summary>
    Task<DeviceStatus> GetStatusAsync(CancellationToken ct);

    /// <summary>等待使用者放入支票、掃描並回傳影像。</summary>
    Task<ScanResult> ScanAsync(ScanRequest request, TimeSpan insertionTimeout, CancellationToken ct);
}
