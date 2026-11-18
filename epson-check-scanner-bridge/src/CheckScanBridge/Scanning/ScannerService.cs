namespace CheckScanBridge.Scanning;

/// <summary>確保同一時間只有一個掃描作業，並支援取消。</summary>
public sealed class ScannerService
{
    private readonly IScannerDevice _device;
    private readonly BridgeOptions _options;
    private readonly ILogger<ScannerService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _current;

    public ScannerService(IScannerDevice device, BridgeOptions options, ILogger<ScannerService> log)
    {
        _device = device;
        _options = options;
        _log = log;
    }

    public string DeviceName => _device.Name;

    public async Task<DeviceStatus> GetStatusAsync(CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0, ct))
            return new DeviceStatus(_device.Name, true, true, "掃描進行中");
        try
        {
            return await _device.GetStatusAsync(ct);
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ScanResult> ScanAsync(ScanRequest request, CancellationToken requestAborted)
    {
        if (!await _lock.WaitAsync(0, requestAborted))
            throw new ScannerException("BUSY", "掃描機正在使用中", StatusCodes.Status409Conflict);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        Volatile.Write(ref _current, cts);
        var timeoutMs = Math.Clamp(request.TimeoutMs ?? _options.DefaultScanTimeoutMs, 1000, _options.MaxScanTimeoutMs);
        try
        {
            _log.LogInformation("Scan started (timeout {Timeout} ms)", timeoutMs);
            var result = await _device.ScanAsync(request, TimeSpan.FromMilliseconds(timeoutMs), cts.Token);
            _log.LogInformation("Scan {ScanId} completed, {Count} image(s)", result.ScanId, result.Images.Count);
            return result;
        }
        catch (OperationCanceledException)
        {
            _log.LogInformation("Scan cancelled");
            throw new ScannerException("CANCELLED", "掃描已取消", StatusCodes.Status409Conflict);
        }
        catch (ScannerException ex)
        {
            _log.LogWarning("Scan failed: {Code} {Message}", ex.Code, ex.Message);
            throw;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Scan failed unexpectedly");
            throw new ScannerException("DEVICE_ERROR", ex.Message, StatusCodes.Status500InternalServerError, ex);
        }
        finally
        {
            Interlocked.CompareExchange(ref _current, null, cts);
            _lock.Release();
        }
    }

    public bool CancelCurrent()
    {
        var cts = Volatile.Read(ref _current);
        if (cts is null) return false;
        try
        {
            cts.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
