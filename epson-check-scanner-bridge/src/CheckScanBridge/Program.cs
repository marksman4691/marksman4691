using System.Reflection;
using System.Text.Json.Serialization;
using CheckScanBridge;
using CheckScanBridge.Scanning;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory, // 以 Windows 服務執行時工作目錄是 System32
});

builder.Host.UseWindowsService(o => o.ServiceName = "CheckScanBridge");

var options = builder.Configuration.GetSection("Bridge").Get<BridgeOptions>() ?? new BridgeOptions();
builder.WebHost.UseUrls(options.Url);

// 本服務自己的 demo 頁面也要能呼叫 API
var selfOrigin = new Uri(options.Url).GetLeftPart(UriPartial.Authority);
var allowedOrigins = new HashSet<string>(
    options.AllowedOrigins.Select(o => o.TrimEnd('/')).Append(selfOrigin),
    StringComparer.OrdinalIgnoreCase);

builder.Services.AddSingleton(options);
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter(allowIntegerValues: false)));
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins.ToArray())
    .WithMethods("GET", "POST")
    .WithHeaders("Content-Type", "X-Bridge-Key")));

if (string.Equals(options.Device, "Opos", StringComparison.OrdinalIgnoreCase))
{
    builder.Services.AddSingleton<IScannerDevice>(sp => OperatingSystem.IsWindows()
        ? new OposCheckScannerDevice(options.Opos, sp.GetRequiredService<ILogger<OposCheckScannerDevice>>())
        : throw new PlatformNotSupportedException("Device=Opos 只能在 Windows 上執行"));
}
else
{
    builder.Services.AddSingleton<IScannerDevice>(new MockScannerDevice(options.Mock));
}
builder.Services.AddSingleton<ScannerService>();

var app = builder.Build();

// Chrome Private Network Access：公開 HTTPS 網站呼叫 127.0.0.1 時，preflight 需回應此 header
app.Use((ctx, next) =>
{
    if (HttpMethods.IsOptions(ctx.Request.Method) &&
        ctx.Request.Headers.ContainsKey("Access-Control-Request-Private-Network"))
    {
        ctx.Response.Headers["Access-Control-Allow-Private-Network"] = "true";
    }
    return next();
});

app.UseCors();

// CORS 只限制「讀取回應」，無法阻止請求送達；因此伺服器端再擋一次 Origin 與 API Key
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api") && !HttpMethods.IsOptions(ctx.Request.Method))
    {
        var origin = ctx.Request.Headers.Origin.ToString();
        var originOk = string.IsNullOrEmpty(origin)
            ? options.AllowRequestsWithoutOrigin
            : allowedOrigins.Contains(origin);
        if (!originOk)
        {
            await Error(ctx, StatusCodes.Status403Forbidden, "ORIGIN_NOT_ALLOWED", $"來源 '{origin}' 未被允許");
            return;
        }
        if (!string.IsNullOrEmpty(options.ApiKey) &&
            !string.Equals(ctx.Request.Headers["X-Bridge-Key"], options.ApiKey, StringComparison.Ordinal))
        {
            await Error(ctx, StatusCodes.Status401Unauthorized, "UNAUTHORIZED", "API Key 錯誤");
            return;
        }
    }
    await next();
});

// 統一錯誤格式
app.Use(async (ctx, next) =>
{
    try
    {
        await next();
    }
    catch (ScannerException ex) when (!ctx.Response.HasStarted)
    {
        await Error(ctx, ex.StatusCode, ex.Code, ex.Message);
    }
});

app.UseDefaultFiles();
app.UseStaticFiles();

var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

app.MapGet("/api/health", (ScannerService s) =>
    Results.Ok(new { status = "ok", version, device = s.DeviceName }));

app.MapGet("/api/device", async (ScannerService s, CancellationToken ct) =>
    Results.Ok(await s.GetStatusAsync(ct)));

app.MapPost("/api/scan", async (ScanRequest? request, ScannerService s, HttpContext ctx) =>
    Results.Ok(await s.ScanAsync(request ?? new ScanRequest(), ctx.RequestAborted)));

app.MapPost("/api/scan/cancel", (ScannerService s) =>
    Results.Ok(new { cancelled = s.CancelCurrent() }));

app.Logger.LogInformation("CheckScanBridge {Version} listening on {Url}, device={Device}",
    version, options.Url, app.Services.GetRequiredService<ScannerService>().DeviceName);

app.Run();

static Task Error(HttpContext ctx, int status, string code, string message)
{
    ctx.Response.StatusCode = status;
    return ctx.Response.WriteAsJsonAsync(new { error = new { code, message } });
}
