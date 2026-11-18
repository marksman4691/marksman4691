using System.Runtime.CompilerServices;
using CheckScanBridge.Capture;
using CheckScanBridge.Drivers;
using CheckScanBridge.Imaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckScanBridge.Tests;

public class CaptureServiceTests
{
    private sealed class FakeDriver : IScannerDriver
    {
        public DriverCapabilities Capabilities { get; init; } = new(true, true, true, true);
        public int FailAfter { get; init; } = int.MaxValue;
        public int Items { get; init; } = 3;
        public string Name => "Fake";
        public Task<DriverStatus> GetStatusAsync(CancellationToken ct) => Task.FromResult(new DriverStatus(true, "ok"));

        public async IAsyncEnumerable<RawItem> CaptureAsync(CaptureOptions options, [EnumeratorCancellation] CancellationToken ct)
        {
            var count = options.Batch ? Items : 1;
            for (var i = 0; i < count; i++)
            {
                await Task.Yield();
                if (i >= FailAfter) throw new ScannerException("PAPER_JAM", "卡紙", 500);
                var g = new GrayImage(1200, 550, 200);
                Array.Fill(g.Pixels, (byte)240);
                for (var x = 100; x < 1100; x++) for (var y = 200; y < 230; y++) g.Pixels[y * 1200 + x] = 20;
                yield return new RawItem(RawImage.FromGray(g), RawImage.FromGray(g), "T021000021T 1234U");
            }
        }
    }

    private static CaptureService Service(IScannerDriver d, Action<BridgeOptions>? cfg = null)
    {
        var o = new BridgeOptions();
        cfg?.Invoke(o);
        return new CaptureService(d, new ItemProcessor(o), o, NullLogger<CaptureService>.Instance);
    }

    [Fact]
    public async Task RejectsDriverWithoutBackImage()
    {
        var s = Service(new FakeDriver { Capabilities = new(true, false, false, false) });
        Assert.Equal(new[] { "BACK_IMAGE", "MICR" }, s.UnmetRequirements());
        var ex = await Assert.ThrowsAsync<ScannerException>(() => s.ScanAsync(new ScanRequest(), default));
        Assert.Equal("CAPABILITY_MISSING", ex.Code);
    }

    [Fact]
    public void BackRequirementCanBeRelaxed()
    {
        var s = Service(new FakeDriver { Capabilities = new(true, false, false, false) },
            o => { o.Requirements.RequireBack = false; o.Requirements.RequireMicr = false; });
        Assert.Empty(s.UnmetRequirements());
    }

    [Fact]
    public async Task SingleModeReturnsOneItem()
    {
        var r = await Service(new FakeDriver()).ScanAsync(new ScanRequest { Preview = false }, default);
        Assert.Single(r.Items);
        Assert.Null(r.Error);
        Assert.Equal("021000021", r.Items[0].Micr!.RoutingNumber);
    }

    [Fact]
    public async Task BatchRespectsMaxItems()
    {
        var r = await Service(new FakeDriver { Items = 5 }).ScanAsync(new ScanRequest { Batch = true, MaxItems = 2, Preview = false }, default);
        Assert.Equal(new[] { 1, 2 }, r.Items.Select(i => i.Sequence));
    }

    [Fact]
    public async Task BatchErrorKeepsScannedItems()
    {
        var r = await Service(new FakeDriver { Items = 5, FailAfter = 2 }).ScanAsync(new ScanRequest { Batch = true, Preview = false }, default);
        Assert.Equal(2, r.Items.Count);
        Assert.Equal("PAPER_JAM", r.Error!.Code);
    }

    [Fact]
    public async Task ErrorBeforeFirstItemThrows()
    {
        var ex = await Assert.ThrowsAsync<ScannerException>(() =>
            Service(new FakeDriver { FailAfter = 0 }).ScanAsync(new ScanRequest(), default));
        Assert.Equal("PAPER_JAM", ex.Code);
    }
}
