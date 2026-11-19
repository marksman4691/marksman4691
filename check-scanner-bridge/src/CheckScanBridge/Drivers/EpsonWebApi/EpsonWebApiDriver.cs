using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CheckScanBridge.Capture;

namespace CheckScanBridge.Drivers.EpsonWebApi;

/// <summary>
/// Epson 網路支票掃描機 (TM-S1000II-NW 等) 驅動，使用掃描機內建的 Scan Web API，不需安裝 Epson 驅動。
/// 依「TM-S1000II-NW Web API Reference Guide Rev.A」：
///   Connect (取得 token 與獨占權) → ScanSetting(Check) → ScanStart(Check)
///   → 輪詢 GetDocumentList → GetImage (正/背面) → Disconnect (刪除掃描機上所有影像)
/// </summary>
public sealed class EpsonWebApiDriver : IScannerDriver, IDisposable
{
    private static readonly HashSet<string> NonFatalResults = new(StringComparer.OrdinalIgnoreCase)
    {
        "", "success", "less_checks", "micr_error", "micr_baddata", "micr_noise", "iqa_not_pass", "ocr_nodata",
    };

    private readonly EpsonWebApiOptions _opt;
    private readonly HttpClient _http;
    private readonly ILogger _log;

    public EpsonWebApiDriver(EpsonWebApiOptions options, ILogger<EpsonWebApiDriver> log, HttpMessageHandler? handler = null)
    {
        _opt = options;
        _log = log;
        _http = new HttpClient(handler ?? new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            BaseAddress = new Uri(options.BaseUrl.TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMilliseconds(options.HttpTimeoutMs),
        };
    }

    public string Name => $"Epson Web API ({new Uri(_opt.BaseUrl).Host})";

    public DriverCapabilities Capabilities { get; } = new(Front: true, Back: true, Micr: true, Batch: true);

    public async Task<DriverStatus> GetStatusAsync(CancellationToken ct)
    {
        try
        {
            using var res = await _http.GetAsync("api/device/status", ct);
            var json = await ReadJson(res, ct);
            if (!res.IsSuccessStatusCode)
                return new DriverStatus(false, $"掃描機回應 HTTP {(int)res.StatusCode}");
            var states = json?["device_status"]?.AsArray().Select(n => n?.GetValue<string>() ?? "").ToList() ?? new();
            var bad = states.Where(s => s is "off_line" or "cover_open" or "mechanical_err" or "unrecover_err" or "autorecover_err").ToList();
            return bad.Count == 0
                ? new DriverStatus(true, $"裝置就緒 ({string.Join(", ", states)})")
                : new DriverStatus(false, $"裝置狀態異常：{string.Join(", ", bad)}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new DriverStatus(false, $"無法連線到掃描機 {_opt.BaseUrl}：{ex.Message}");
        }
    }

    public async IAsyncEnumerable<RawItem> CaptureAsync(CaptureOptions options, [EnumeratorCancellation] CancellationToken ct)
    {
        var token = await Connect(ct);
        var scanning = false;
        try
        {
            await Send(HttpMethod.Post, "api/scan/setting/check", token, BuildScanSetting(), ct);

            var limit = options.Batch ? Math.Clamp(options.MaxItems, 1, 100) : 1;
            var insertSeconds = (int)Math.Clamp(Math.Ceiling(options.InsertionTimeout.TotalSeconds), 1, 300);
            await Send(HttpMethod.Post, "api/scan/start/check", token, new JsonObject
            {
                ["limit"] = limit,
                ["timeout"] = insertSeconds,
                ["transaction_number"] = null,
                ["step"] = 1,
            }, ct);
            scanning = true;

            var emitted = new HashSet<long>();
            var deadline = DateTime.UtcNow + options.InsertionTimeout + TimeSpan.FromMilliseconds(_opt.CompletionTimeoutMs);
            while (true)
            {
                await Task.Delay(_opt.PollIntervalMs, ct);
                var list = await Send(HttpMethod.Get, "api/docs", token, null, ct)
                           ?? throw Error("DEVICE_ERROR", "掃描機未回傳文件清單");
                var status = list["status"]?.GetValue<string>() ?? "";
                var result = list["latest_result"]?.GetValue<string>() ?? "";
                var finished = status != "scanning" && status != "printing" && result != "";

                var docs = (list["documents"] as JsonArray ?? new JsonArray())
                    .OfType<JsonObject>()
                    .Select(d => (No: d["transaction_number"]!.GetValue<long>(), Doc: d))
                    .Where(d => !emitted.Contains(d.No))
                    .OrderBy(d => d.No)
                    .ToList();
                // 掃描進行中時，最後一張可能仍在處理，等下一張出現或掃描結束才取
                var ready = finished ? docs : docs.Take(Math.Max(0, docs.Count - 1)).ToList();
                foreach (var (no, doc) in ready)
                {
                    emitted.Add(no);
                    yield return await ToRawItem(token, doc, ct);
                    deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(_opt.CompletionTimeoutMs);
                }

                if (finished)
                {
                    scanning = false;
                    if (NonFatalResults.Contains(result)) yield break;
                    if (result == "no_docs" && emitted.Count > 0) yield break;
                    if (result == "canceled") ct.ThrowIfCancellationRequested();
                    throw MapScanResult(result);
                }
                if (DateTime.UtcNow > deadline)
                    throw Error("TIMEOUT", "掃描機未在預期時間內完成", StatusCodes.Status408RequestTimeout);
            }
        }
        finally
        {
            await Cleanup(token, scanning);
        }
    }

    private async Task<string> Connect(CancellationToken ct)
    {
        HttpResponseMessage res;
        try
        {
            res = await _http.PostAsJsonAsync("api/connect",
                new { timeout = Math.Clamp(_opt.ExclusiveTimeoutSeconds, 10, 3600) }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw Error("DEVICE_OFFLINE", $"無法連線到掃描機 {_opt.BaseUrl}：{ex.Message}", StatusCodes.Status503ServiceUnavailable);
        }
        using (res)
        {
            var json = await ReadJson(res, ct);
            if (res.StatusCode == HttpStatusCode.BadRequest && Code(json) == "device_busy")
                throw Error("BUSY", "掃描機正被其他電腦使用中", StatusCodes.Status409Conflict);
            if (!res.IsSuccessStatusCode)
                throw Error("DEVICE_ERROR", $"Connect 失敗 (HTTP {(int)res.StatusCode} {Code(json)})");
            return json?["token"]?.GetValue<string>() ?? throw Error("DEVICE_ERROR", "Connect 未回傳 token");
        }
    }

    private JsonObject BuildScanSetting()
    {
        var setting = new JsonObject
        {
            ["face"] = "both",
            ["images"] = new JsonArray(new JsonObject
            {
                ["format"] = "tiff",             // CCITT G4，符合 ANSI X9.100-181
                ["type"] = "black-and-white",
                ["dual_image"] = false,
            }),
            ["double_feed_stop"] = _opt.StopOnDoubleFeed,
            ["micr"] = new JsonObject
            {
                ["enabled"] = true,
                ["font"] = "E13B",
                ["parsing"] = true,
                ["error_stop"] = _opt.StopOnMicrError,
            },
            ["iqa"] = new JsonObject
            {
                ["enabled"] = _opt.IqaEnabled,
                ["error_stop"] = false,
            },
        };
        if (!string.IsNullOrWhiteSpace(_opt.ElectronicEndorsement))
        {
            setting["endorse"] = new JsonObject
            {
                ["type"] = "electronic",
                ["data"] = new JsonArray(new JsonObject
                {
                    ["font_type"] = "sans-serif",
                    ["font_size"] = 10,
                    ["top_left_x"] = 10,
                    ["top_left_y"] = 5,
                    ["width"] = 120,
                    ["height"] = 8,
                    ["direction"] = "left_to_right",
                    ["contents"] = _opt.ElectronicEndorsement,
                }),
            };
        }
        return setting;
    }

    private async Task<RawItem> ToRawItem(string token, JsonObject doc, CancellationToken ct)
    {
        var warnings = new List<string>();
        var front = await GetImage(token, doc["front"] as JsonArray, ct);
        var back = await GetImage(token, doc["back"] as JsonArray, ct);

        string? micr = null;
        if (doc["micr"] is JsonObject m && m["text"]?.GetValue<string>() is { Length: > 0 } text)
        {
            micr = NormalizeMicr(text);
            // 用 Epson 自己解析的 routing 交叉檢查符號對照表是否正確
            var epsonTransit = m["transit_number"]?.GetValue<string>();
            var ours = MicrParser.Parse(micr).RoutingNumber;
            if (!string.IsNullOrEmpty(epsonTransit) && epsonTransit != ours)
                warnings.Add("MICR_PARSE_MISMATCH");
        }

        if (doc["iqa"] is JsonObject iqa)
            foreach (var (name, node) in iqa)
                if (node?["result"]?.GetValue<string>() == "not_pass")
                    warnings.Add("IQA_" + name.ToUpperInvariant());

        return new RawItem(front, back, micr, warnings);
    }

    private async Task<RawImage?> GetImage(string token, JsonArray? uris, CancellationToken ct)
    {
        var uri = uris?.Select(u => u?.GetValue<string>()).FirstOrDefault(u =>
                      u is not null && (u.EndsWith(".tif", StringComparison.OrdinalIgnoreCase) || u.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)))
                  ?? uris?.Select(u => u?.GetValue<string>()).FirstOrDefault(u => u is not null);
        if (uri is null) return null;

        using var req = new HttpRequestMessage(HttpMethod.Get, uri.TrimStart('/'));
        Authorize(req, token);
        using var res = await _http.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode)
            throw Error("DEVICE_ERROR", $"GetImage {uri} 失敗 (HTTP {(int)res.StatusCode})");
        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        return RawImage.FromTiff(bytes, 200);
    }

    internal string NormalizeMicr(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
        {
            var s = c.ToString();
            sb.Append(_opt.MicrSymbolMap.TryGetValue(s, out var mapped) ? mapped
                : char.IsAsciiDigit(c) || c == ' ' ? s
                : "?");
        }
        return sb.ToString();
    }

    private async Task Cleanup(string token, bool scanning)
    {
        // 使用者取消或瀏覽器中斷時也要釋放掃描機，避免其他電腦被鎖住直到逾時
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            if (scanning) await Send(HttpMethod.Post, "api/scan/cancel", token, null, cts.Token, throwOnError: false);
            await Send(HttpMethod.Post, "api/disconnect", token, null, cts.Token, throwOnError: false);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Epson disconnect failed: {Message}", ex.Message);
        }
    }

    private async Task<JsonObject?> Send(HttpMethod method, string path, string token, JsonObject? body,
        CancellationToken ct, bool throwOnError = true)
    {
        using var req = new HttpRequestMessage(method, path);
        Authorize(req, token);
        if (body is not null)
            req.Content = new StringContent(body.ToJsonString(), new UTF8Encoding(false), "application/json");
        HttpResponseMessage res;
        try
        {
            res = await _http.SendAsync(req, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested && throwOnError)
        {
            throw Error("DEVICE_OFFLINE", $"與掃描機連線中斷：{ex.Message}", StatusCodes.Status503ServiceUnavailable);
        }
        using (res)
        {
            var json = await ReadJson(res, ct);
            if (res.IsSuccessStatusCode || !throwOnError) return json;
            var code = Code(json);
            throw (res.StatusCode, code) switch
            {
                (HttpStatusCode.Unauthorized, _) => Error("DEVICE_ERROR", "與掃描機的連線已逾時或被中斷"),
                (HttpStatusCode.BadRequest, "device_busy") => Error("BUSY", "掃描機忙碌中或紙張通道內有紙", StatusCodes.Status409Conflict),
                _ => Error("DEVICE_ERROR", $"{method} /{path} 失敗 (HTTP {(int)res.StatusCode} {code})"),
            };
        }
    }

    private void Authorize(HttpRequestMessage req, string token) =>
        req.Headers.TryAddWithoutValidation("Authorization",
            string.IsNullOrWhiteSpace(_opt.AuthorizationScheme) ? token : $"{_opt.AuthorizationScheme} {token}");

    private static async Task<JsonObject?> ReadJson(HttpResponseMessage res, CancellationToken ct)
    {
        if (res.Content.Headers.ContentType?.MediaType != "application/json") return null;
        try
        {
            return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct)) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string Code(JsonObject? json) => json?["code"]?.GetValue<string>() ?? "";

    private static ScannerException MapScanResult(string result) => result switch
    {
        "no_docs" => Error("TIMEOUT", "等待放入支票逾時", StatusCodes.Status408RequestTimeout),
        "timeout" => Error("TIMEOUT", "掃描逾時", StatusCodes.Status408RequestTimeout),
        "jammed" => Error("PAPER_JAM", "卡紙，請取出支票後重試"),
        "double_feed" => Error("DOUBLE_FEED", "偵測到重疊進紙"),
        "cover_open" => Error("COVER_OPEN", "掃描機上蓋開啟", StatusCodes.Status503ServiceUnavailable),
        "paper_exist" => Error("PAPER_IN_PATH", "紙張通道內有紙，請取出", StatusCodes.Status409Conflict),
        "misinsertion" => Error("MISINSERTION", "支票放置錯誤，請重新放入"),
        "disk_full" => Error("DEVICE_MEMORY_FULL", "掃描機記憶體不足"),
        _ => Error("DEVICE_ERROR", $"掃描失敗 ({result})"),
    };

    private static ScannerException Error(string code, string message, int status = StatusCodes.Status500InternalServerError) =>
        new(code, message, status);

    public void Dispose() => _http.Dispose();
}
