using System.Runtime.CompilerServices;
using CheckScanBridge.Capture;
using CheckScanBridge.Imaging;

namespace CheckScanBridge.Drivers.Mock;

/// <summary>
/// 模擬掃描機：產生帶底紋的灰階正反面影像 + MICR，支援連續掃描。
/// 用於網頁端開發與驗證共用處理管線 (二值化、重新取樣、TIFF G4、MICR 解析)。
/// </summary>
public sealed class MockScannerDriver : IScannerDriver
{
    private static readonly string[] SampleMicr =
    {
        "T021000021T 1234567890U 0123",          // 個人支票：帳號後接票號
        "U004567U T011000015T 98765432U",        // 商業支票：票號在 Aux On-Us
        "T111000025T 55500012U 0457 A0000012500A", // 已印金額 $125.00
    };

    private readonly MockOptions _options;

    public MockScannerDriver(MockOptions options) => _options = options;

    public string Name => "Mock";

    public DriverCapabilities Capabilities { get; } = new(Front: true, Back: true, Micr: true, Batch: true);

    public Task<DriverStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new DriverStatus(true, "模擬裝置就緒"));

    public async IAsyncEnumerable<RawItem> CaptureAsync(CaptureOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var wait = TimeSpan.FromMilliseconds(_options.InsertionDelayMs);
        if (wait > options.InsertionTimeout)
        {
            await Task.Delay(options.InsertionTimeout, ct);
            throw new ScannerException("TIMEOUT", "等待放入支票逾時", StatusCodes.Status408RequestTimeout);
        }
        await Task.Delay(wait, ct);

        var count = options.Batch ? Math.Min(_options.ItemsInHopper, options.MaxItems) : 1;
        for (var i = 0; i < count; i++)
        {
            if (i > 0) await Task.Delay(_options.PerItemDelayMs, ct);
            var seed = i;
            yield return await Task.Run(() => new RawItem(
                RawImage.FromGray(FakeCheck.Render(_options.Dpi, back: false, seed)),
                RawImage.FromGray(FakeCheck.Render(_options.Dpi, back: true, seed)),
                SampleMicr[seed % SampleMicr.Length]), ct);
        }
    }
}

internal static class FakeCheck
{
    /// <summary>6" × 2.75" 個人支票尺寸，含淺色防偽底紋。</summary>
    public static GrayImage Render(int dpi, bool back, int seed)
    {
        int w = (int)(6.0 * dpi), h = (int)(2.75 * dpi);
        var img = new GrayImage(w, h, dpi);
        var px = img.Pixels;
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                // 底紋：淺灰斜紋 + 漸層 (應被二值化濾除)
                var pattern = ((x + y + seed * 7) / Math.Max(1, dpi / 25)) % 2 == 0 ? 12 : 0;
                px[y * w + x] = (byte)(238 - pattern - x * 10 / w);
            }

        void Rect(double x0, double y0, double x1, double y1, byte v)
        {
            for (var y = (int)(y0 * dpi); y < Math.Min(h, (int)(y1 * dpi)); y++)
                for (var x = (int)(x0 * dpi); x < Math.Min(w, (int)(x1 * dpi)); x++)
                    px[y * w + x] = v;
        }

        Rect(0, 0, 6, 0.02, 40); Rect(0, 2.73, 6, 2.75, 40);
        Rect(0, 0, 0.02, 2.75, 40); Rect(5.98, 0, 6, 2.75, 40);
        if (!back)
        {
            Rect(0.2, 0.15, 2.2 + seed * 0.2, 0.27, 30);   // 付款人名稱
            Rect(0.2, 0.32, 1.6, 0.38, 60);                // 地址
            Rect(4.3, 0.35, 5.7, 0.37, 50);                // 日期線
            Rect(0.2, 0.95, 4.3, 0.965, 50);               // 受款人線
            Rect(4.5, 0.75, 5.7, 1.0, 90);                 // 金額框
            Rect(0.2, 1.4, 5.7, 1.415, 50);                // 大寫金額線
            Rect(3.6, 2.0, 5.7, 2.015, 50);                // 簽名線
            for (var i = 0; i < 32; i++)                   // MICR 碼帶 (底部 5/8 英吋)
                Rect(0.4 + i * 0.13, 2.42, 0.4 + i * 0.13 + 0.08, 2.54, 20);
        }
        else
        {
            Rect(0.3, 0.3, 2.3, 0.315, 70);                // 背書線
            Rect(0.3, 0.6, 2.3, 0.615, 70);
            Rect(0.3, 0.9, 2.3, 0.915, 70);
            Rect(0.35, 0.4, 1.8, 0.5, 35);                 // 背書內容
        }
        return img;
    }
}
