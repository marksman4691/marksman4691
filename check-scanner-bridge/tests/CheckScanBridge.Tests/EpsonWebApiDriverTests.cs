using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using CheckScanBridge.Capture;
using CheckScanBridge.Drivers;
using CheckScanBridge.Drivers.EpsonWebApi;
using CheckScanBridge.Imaging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CheckScanBridge.Tests;

/// <summary>依 TM-S1000II-NW Web API Reference Guide 行為模擬的假掃描機。</summary>
internal sealed class FakeEpsonScanner : HttpMessageHandler
{
    public const string Token = "550e8400-e29b-41d4-a716-446655440000";

    public bool Busy { get; init; }
    public List<JsonObject> Docs { get; init; } = new();
    public string FinalResult { get; init; } = "success";
    /// <summary>每次輪詢釋出幾張文件 (模擬逐張進紙)。</summary>
    public int DocsPerPoll { get; init; } = 1;

    public List<string> Calls { get; } = new();
    public JsonObject? LastSetting { get; private set; }
    public JsonObject? LastStart { get; private set; }
    public bool Connected { get; private set; }

    private int _released;
    private bool _scanning;
    private bool _canceled;
    private readonly HashSet<string> _fetched = new();
    private static readonly byte[] Tiff = X9TiffCodec.Encode(Sample(), TiffByteOrder.LittleEndian);

    private static BitonalImage Sample()
    {
        var img = new BitonalImage(1200, 550, 200);
        for (var y = 100; y < 140; y++) for (var x = 100; x < 1100; x++) img.SetBlack(x, y);
        return img;
    }

    public static JsonObject Doc(long no, string micrText, string transit, JsonObject? iqa = null) => new()
    {
        ["transaction_number"] = no,
        ["front"] = new JsonArray($"/api/docs/{no}/{no}_check_front.tif"),
        ["back"] = new JsonArray($"/api/docs/{no}/{no}_check_back.tif"),
        ["micr"] = new JsonObject { ["text"] = micrText, ["transit_number"] = transit },
        ["iqa"] = iqa,
    };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var path = req.RequestUri!.AbsolutePath;
        Calls.Add($"{req.Method} {path}");
        var body = req.Content is null ? null : JsonNode.Parse(await req.Content.ReadAsStringAsync(ct)) as JsonObject;

        if (path == "/api/device/status") return Json(200, new JsonObject { ["device_status"] = new JsonArray("ok") });
        if (path == "/api/connect")
        {
            if (Busy || Connected) return Json(400, new JsonObject { ["code"] = "device_busy" });
            Connected = true;
            return Json(200, new JsonObject { ["token"] = Token, ["hardware"] = new JsonObject { ["device_name"] = "TM-S1000II-NW" } });
        }
        if (!Connected || req.Headers.GetValues("Authorization").Single() != Token)
            return Json(401, new JsonObject { ["code"] = "access_token_verification_failed" });

        switch (req.Method.Method, path)
        {
            case ("POST", "/api/scan/setting/check"):
                LastSetting = body;
                return Json(200, new JsonObject());
            case ("POST", "/api/scan/start/check"):
                LastStart = body;
                _scanning = true;
                return Json(200, new JsonObject());
            case ("POST", "/api/scan/cancel"):
                if (!_scanning) return Json(400, new JsonObject { ["code"] = "not_scanning" });
                _scanning = false;
                _canceled = true;
                return Json(200, new JsonObject());
            case ("POST", "/api/disconnect"):
                Connected = false;
                return Json(200, new JsonObject());
            case ("GET", "/api/docs"):
            {
                var limit = LastStart?["limit"]?.GetValue<int>() ?? 1;
                var available = Math.Min(Docs.Count, limit);
                if (_scanning) _released = Math.Min(available, _released + DocsPerPoll);
                var done = _scanning && _released >= available;
                if (done) _scanning = false;
                var result = _scanning ? "" : _canceled ? "canceled"
                    : _released < limit && FinalResult == "success" && _released > 0 ? "less_checks" : FinalResult;
                var docs = new JsonArray();
                foreach (var d in Docs.Take(_released))
                    if (!_fetched.Contains(d["transaction_number"]!.ToString())) docs.Add(d.DeepClone());
                return Json(200, new JsonObject
                {
                    ["latest_result"] = result,
                    ["status"] = _scanning ? "scanning" : "connected",
                    ["documents"] = docs,
                });
            }
        }
        if (req.Method == HttpMethod.Get && path.StartsWith("/api/docs/"))
        {
            var no = path.Split('/')[3];
            if (path.EndsWith("_back.tif")) _fetched.Add(no);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Tiff) { Headers = { { "Content-Type", "image/tiff" } } },
            };
        }
        return Json(404, new JsonObject { ["code"] = "not_found" });
    }

    private static HttpResponseMessage Json(int status, JsonObject body) => new((HttpStatusCode)status)
    {
        Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
    };
}

public class EpsonWebApiDriverTests
{
    private static EpsonWebApiDriver Driver(FakeEpsonScanner fake, Action<EpsonWebApiOptions>? cfg = null)
    {
        var o = new EpsonWebApiOptions { BaseUrl = "http://scanner.test", PollIntervalMs = 1 };
        cfg?.Invoke(o);
        return new EpsonWebApiDriver(o, NullLogger<EpsonWebApiDriver>.Instance, fake);
    }

    private static CaptureService Service(IScannerDriver d) =>
        new(d, new ItemProcessor(new BridgeOptions()), new BridgeOptions(), NullLogger<CaptureService>.Instance);

    private static readonly CaptureOptions Single = new() { InsertionTimeout = TimeSpan.FromSeconds(30), MaxItems = 1 };

    [Fact]
    public async Task SingleScanFollowsEpsonFlow()
    {
        var fake = new FakeEpsonScanner { Docs = { FakeEpsonScanner.Doc(1, "t021000021t1234567890o 0123", "021000021") } };
        var items = await Driver(fake).CaptureAsync(Single, default).ToListAsync();

        var item = Assert.Single(items);
        Assert.NotNull(item.Front?.Tiff);
        Assert.NotNull(item.Back?.Tiff);
        Assert.Equal("T021000021T1234567890U 0123", item.MicrRaw);
        Assert.Empty(item.DriverWarnings!);

        Assert.Equal("both", fake.LastSetting!["face"]!.GetValue<string>());
        Assert.Equal("tiff", fake.LastSetting["images"]![0]!["format"]!.GetValue<string>());
        Assert.Equal("black-and-white", fake.LastSetting["images"]![0]!["type"]!.GetValue<string>());
        Assert.Equal(1, fake.LastStart!["limit"]!.GetValue<int>());
        Assert.Equal(30, fake.LastStart["timeout"]!.GetValue<int>());
        Assert.Equal("POST /api/connect", fake.Calls.First());
        Assert.Equal("POST /api/disconnect", fake.Calls.Last());
        Assert.False(fake.Connected);
    }

    [Fact]
    public async Task EndToEndThroughPipelineProducesX9Item()
    {
        var fake = new FakeEpsonScanner { Docs = { FakeEpsonScanner.Doc(1, "o004567o t011000015t98765432o", "011000015") } };
        var r = await Service(Driver(fake)).ScanAsync(new ScanRequest { Preview = false }, default);
        var item = Assert.Single(r.Items);
        Assert.Equal("x9-tiff-g4", item.Back.Format);
        Assert.Equal("011000015", item.Micr!.RoutingNumber);
        Assert.True(item.Micr.RoutingValid);
        Assert.Equal("004567", item.Micr.AuxiliaryOnUs);
        Assert.Empty(item.Warnings);
    }

    [Fact]
    public async Task BatchStreamsAllDocuments()
    {
        var fake = new FakeEpsonScanner
        {
            Docs =
            {
                FakeEpsonScanner.Doc(1, "t021000021t111o 1", "021000021"),
                FakeEpsonScanner.Doc(2, "t021000021t222o 2", "021000021"),
                FakeEpsonScanner.Doc(3, "t021000021t333o 3", "021000021"),
            },
        };
        var items = await Driver(fake).CaptureAsync(
            new CaptureOptions { InsertionTimeout = TimeSpan.FromSeconds(10), Batch = true, MaxItems = 50 }, default).ToListAsync();
        Assert.Equal(new[] { "T021000021T111U 1", "T021000021T222U 2", "T021000021T333U 3" }, items.Select(i => i.MicrRaw));
        Assert.Equal(50, fake.LastStart!["limit"]!.GetValue<int>());
    }

    [Fact]
    public async Task BusyScannerIsReported()
    {
        var ex = await Assert.ThrowsAsync<ScannerException>(() =>
            Driver(new FakeEpsonScanner { Busy = true }).CaptureAsync(Single, default).ToListAsync().AsTask());
        Assert.Equal("BUSY", ex.Code);
    }

    [Fact]
    public async Task NoCheckInsertedIsTimeout()
    {
        var fake = new FakeEpsonScanner { FinalResult = "no_docs" };
        var ex = await Assert.ThrowsAsync<ScannerException>(() => Driver(fake).CaptureAsync(Single, default).ToListAsync().AsTask());
        Assert.Equal("TIMEOUT", ex.Code);
        Assert.False(fake.Connected);
    }

    [Fact]
    public async Task JamAfterFirstCheckKeepsScannedItem()
    {
        var fake = new FakeEpsonScanner
        {
            Docs = { FakeEpsonScanner.Doc(1, "t021000021t1o", "021000021") },
            FinalResult = "jammed",
        };
        var r = await Service(Driver(fake)).ScanAsync(new ScanRequest { Batch = true, Preview = false }, default);
        Assert.Single(r.Items);
        Assert.Equal("PAPER_JAM", r.Error!.Code);
    }

    [Fact]
    public async Task IqaFailuresBecomeWarnings()
    {
        var iqa = new JsonObject
        {
            ["image_too_light"] = new JsonObject { ["result"] = "not_pass" },
            ["piggy_back"] = new JsonObject { ["result"] = "pass" },
        };
        var fake = new FakeEpsonScanner { Docs = { FakeEpsonScanner.Doc(1, "t021000021t1o", "021000021", iqa) }, FinalResult = "iqa_not_pass" };
        var item = Assert.Single(await Driver(fake).CaptureAsync(Single, default).ToListAsync());
        Assert.Equal(new[] { "IQA_IMAGE_TOO_LIGHT" }, item.DriverWarnings);
    }

    [Fact]
    public async Task WrongSymbolMapIsDetected()
    {
        var fake = new FakeEpsonScanner { Docs = { FakeEpsonScanner.Doc(1, "t021000021t1o", "021000021") } };
        var item = Assert.Single(await Driver(fake, o => o.MicrSymbolMap = new() { ["o"] = "U" })
            .CaptureAsync(Single, default).ToListAsync());
        Assert.Contains("MICR_PARSE_MISMATCH", item.DriverWarnings!);
        Assert.Equal("?021000021?1U", item.MicrRaw);
    }

    [Fact]
    public async Task CancelStopsScanAndDisconnects()
    {
        var fake = new FakeEpsonScanner { Docs = { FakeEpsonScanner.Doc(1, "t021000021t1o", "021000021") }, DocsPerPoll = 0 };
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Driver(fake, o => o.PollIntervalMs = 20).CaptureAsync(Single, cts.Token).ToListAsync().AsTask());
        Assert.Contains("POST /api/scan/cancel", fake.Calls);
        Assert.Equal("POST /api/disconnect", fake.Calls.Last());
        Assert.False(fake.Connected);
    }

    [Fact]
    public async Task EndorsementIsSentWhenConfigured()
    {
        var fake = new FakeEpsonScanner { Docs = { FakeEpsonScanner.Doc(1, "t021000021t1o", "021000021") } };
        await Driver(fake, o => o.ElectronicEndorsement = "FOR DEPOSIT ONLY").CaptureAsync(Single, default).ToListAsync();
        Assert.Equal("electronic", fake.LastSetting!["endorse"]!["type"]!.GetValue<string>());
        Assert.Equal("FOR DEPOSIT ONLY", fake.LastSetting["endorse"]!["data"]![0]!["contents"]!.GetValue<string>());
    }

    [Fact]
    public async Task StatusReportsDeviceState()
    {
        var s = await Driver(new FakeEpsonScanner()).GetStatusAsync(default);
        Assert.True(s.Available);
    }
}

internal static class AsyncEnumerableExtensions
{
    public static async ValueTask<List<T>> ToListAsync<T>(this IAsyncEnumerable<T> source)
    {
        var list = new List<T>();
        await foreach (var x in source) list.Add(x);
        return list;
    }
}
