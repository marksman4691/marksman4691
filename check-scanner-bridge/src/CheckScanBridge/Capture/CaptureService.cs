using CheckScanBridge.Drivers;

namespace CheckScanBridge.Capture;

/// <summary>確保同一時間只有一個掃描作業，檢查需求、支援取消，並把原始資料交給處理管線。</summary>
public sealed class CaptureService
{
    private readonly IScannerDriver _driver;
    private readonly ItemProcessor _processor;
    private readonly BridgeOptions _options;
    private readonly ILogger<CaptureService> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private CancellationTokenSource? _current;

    public CaptureService(IScannerDriver driver, ItemProcessor processor, BridgeOptions options, ILogger<CaptureService> log)
    {
        _driver = driver;
        _processor = processor;
        _options = options;
        _log = log;
    }

    public string DeviceName => _driver.Name;

    public IReadOnlyList<string> UnmetRequirements()
    {
        var caps = _driver.Capabilities;
        var unmet = new List<string>();
        if (!caps.Front) unmet.Add("FRONT_IMAGE");
        if (_options.Requirements.RequireBack && !caps.Back) unmet.Add("BACK_IMAGE");
        if (_options.Requirements.RequireMicr && !caps.Micr) unmet.Add("MICR");
        return unmet;
    }

    public async Task<DeviceStatus> GetStatusAsync(CancellationToken ct)
    {
        if (!await _lock.WaitAsync(0, ct))
            return new DeviceStatus(_driver.Name, true, true, "掃描進行中", _driver.Capabilities, UnmetRequirements());
        try
        {
            var s = await _driver.GetStatusAsync(ct);
            return new DeviceStatus(_driver.Name, s.Available, false, s.Message, _driver.Capabilities, UnmetRequirements());
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task<ScanResult> ScanAsync(ScanRequest request, CancellationToken requestAborted)
    {
        var unmet = UnmetRequirements();
        if (unmet.Count > 0)
            throw new ScannerException("CAPABILITY_MISSING",
                $"驅動 {_driver.Name} 不支援必要功能：{string.Join(", ", unmet)}", StatusCodes.Status501NotImplemented);

        if (!await _lock.WaitAsync(0, requestAborted))
            throw new ScannerException("BUSY", "掃描機正在使用中", StatusCodes.Status409Conflict);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
        Volatile.Write(ref _current, cts);
        var batch = request.Batch && _driver.Capabilities.Batch;
        var options = new CaptureOptions
        {
            InsertionTimeout = TimeSpan.FromMilliseconds(
                Math.Clamp(request.TimeoutMs ?? _options.DefaultScanTimeoutMs, 1000, _options.MaxScanTimeoutMs)),
            Batch = batch,
            MaxItems = batch ? Math.Clamp(request.MaxItems ?? _options.MaxBatchItems, 1, _options.MaxBatchItems) : 1,
            PreferredDpi = _options.Image.Dpi,
        };

        var scanId = Guid.NewGuid().ToString("N");
        var items = new List<CheckItem>();
        try
        {
            _log.LogInformation("Scan {ScanId} started (batch={Batch})", scanId, batch);
            await foreach (var raw in _driver.CaptureAsync(options, cts.Token))
            {
                items.Add(_processor.Process(raw, items.Count + 1, request.Preview));
                if (items.Count >= options.MaxItems) break;
            }
            if (items.Count == 0)
                throw new ScannerException("NO_ITEMS", "沒有掃描到任何支票");
            _log.LogInformation("Scan {ScanId} completed, {Count} item(s)", scanId, items.Count);
            return Result(null);
        }
        catch (Exception ex) when (items.Count > 0)
        {
            // 已經實體掃描過的支票不能遺失：回傳部分結果並附上錯誤
            var err = Translate(ex);
            _log.LogWarning("Scan {ScanId} stopped after {Count} item(s): {Code}", scanId, items.Count, err.Code);
            return Result(new ScanError(err.Code, err.Message));
        }
        catch (Exception ex)
        {
            var err = Translate(ex);
            if (err.StatusCode >= 500) _log.LogError(ex, "Scan {ScanId} failed", scanId);
            else _log.LogInformation("Scan {ScanId} ended: {Code}", scanId, err.Code);
            throw err;
        }
        finally
        {
            Interlocked.CompareExchange(ref _current, null, cts);
            _lock.Release();
        }

        ScanResult Result(ScanError? error) =>
            new(scanId, DateTimeOffset.Now, _driver.Name, items, error);
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

    private static ScannerException Translate(Exception ex) => ex switch
    {
        ScannerException s => s,
        OperationCanceledException => new ScannerException("CANCELLED", "掃描已取消", StatusCodes.Status409Conflict),
        _ => new ScannerException("DEVICE_ERROR", ex.Message, StatusCodes.Status500InternalServerError, ex),
    };
}
