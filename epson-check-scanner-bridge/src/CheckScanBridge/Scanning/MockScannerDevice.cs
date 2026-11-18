namespace CheckScanBridge.Scanning;

/// <summary>
/// 模擬掃描機：不需實體設備即可開發/測試網頁端整合。
/// 產生一張灰階 BMP 假支票影像 (正面 + 背面)。
/// </summary>
public sealed class MockScannerDevice : IScannerDevice
{
    private readonly MockOptions _options;

    public MockScannerDevice(MockOptions options) => _options = options;

    public string Name => "Mock";

    public Task<DeviceStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new DeviceStatus(Name, true, false, "模擬裝置就緒"));

    public async Task<ScanResult> ScanAsync(ScanRequest request, TimeSpan insertionTimeout, CancellationToken ct)
    {
        var delay = TimeSpan.FromMilliseconds(_options.InsertionDelayMs);
        if (delay > insertionTimeout)
        {
            await Task.Delay(insertionTimeout, ct);
            throw new ScannerException("TIMEOUT", "等待放入支票逾時", StatusCodes.Status408RequestTimeout);
        }
        await Task.Delay(delay, ct);

        var front = FakeCheckBitmap.Create(600, 275, back: false);
        var back = FakeCheckBitmap.Create(600, 275, back: true);
        return new ScanResult(
            Guid.NewGuid().ToString("N"),
            DateTimeOffset.Now,
            Name,
            new[]
            {
                new ScanImage("front", "image/bmp", front.Length, Convert.ToBase64String(front)),
                new ScanImage("back", "image/bmp", back.Length, Convert.ToBase64String(back)),
            },
            "T021000021T 1234567890U 000123");
    }
}

internal static class FakeCheckBitmap
{
    /// <summary>產生 8-bit 灰階 BMP。</summary>
    public static byte[] Create(int width, int height, bool back)
    {
        var px = new byte[width * height];
        Array.Fill(px, (byte)245);

        void Rect(int x0, int y0, int x1, int y1, byte v)
        {
            for (var y = Math.Max(0, y0); y < Math.Min(height, y1); y++)
                for (var x = Math.Max(0, x0); x < Math.Min(width, x1); x++)
                    px[y * width + x] = v;
        }

        // 外框
        Rect(0, 0, width, 3, 60); Rect(0, height - 3, width, height, 60);
        Rect(0, 0, 3, height, 60); Rect(width - 3, 0, width, height, 60);

        if (!back)
        {
            Rect(20, 20, 220, 30, 90);                       // 銀行名稱
            Rect(width - 160, 20, width - 20, 28, 120);      // 日期
            Rect(20, 90, width - 180, 92, 80);               // 受款人線
            Rect(width - 160, 70, width - 20, 100, 200);     // 金額框
            Rect(20, 140, width - 20, 142, 80);              // 金額大寫線
            Rect(width - 220, 200, width - 20, 202, 80);     // 簽名線
            for (var i = 0; i < 30; i++)                     // MICR 碼帶
                Rect(30 + i * 15, height - 35, 30 + i * 15 + 8, height - 20, 40);
        }
        else
        {
            Rect(30, 40, 250, 42, 120);                      // 背書線
            Rect(30, 70, 250, 72, 120);
            Rect(width - 240, 30, width - 40, 120, 200);     // 銀行章
        }

        const int headerSize = 14 + 40 + 256 * 4;
        var stride = (width + 3) & ~3;
        var imageSize = stride * height;
        var buf = new byte[headerSize + imageSize];
        using var ms = new MemoryStream(buf);
        using var w = new BinaryWriter(ms);
        w.Write((byte)'B'); w.Write((byte)'M');
        w.Write(buf.Length); w.Write(0); w.Write(headerSize);
        w.Write(40); w.Write(width); w.Write(height);
        w.Write((short)1); w.Write((short)8); w.Write(0); w.Write(imageSize);
        w.Write(7874); w.Write(7874); w.Write(256); w.Write(0);   // 200 dpi
        for (var i = 0; i < 256; i++) { w.Write((byte)i); w.Write((byte)i); w.Write((byte)i); w.Write((byte)0); }
        for (var y = height - 1; y >= 0; y--)   // BMP 由下而上
        {
            w.Write(px, y * width, width);
            for (var p = width; p < stride; p++) w.Write((byte)0);
        }
        return buf;
    }
}
