using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace CheckScanBridge.Tests;

public class ApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(b =>
        {
            b.UseSetting("Bridge:Driver", "Mock");
            b.UseSetting("Bridge:Mock:InsertionDelayMs", "10");
            b.UseSetting("Bridge:Mock:PerItemDelayMs", "10");
        });
    }

    [Fact]
    public async Task ScanReturnsFrontBackAndMicr()
    {
        var client = _factory.CreateClient();
        var res = await client.PostAsJsonAsync("/api/scan", new { batch = true, maxItems = 2 });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items");
        Assert.Equal(2, items.GetArrayLength());
        var first = items[0];
        Assert.Equal("x9-tiff-g4", first.GetProperty("front").GetProperty("format").GetString());
        Assert.Equal(200, first.GetProperty("back").GetProperty("dpi").GetInt32());
        Assert.Equal("021000021", first.GetProperty("micr").GetProperty("routingNumber").GetString());
        Assert.Equal(0, first.GetProperty("warnings").GetArrayLength());
    }

    [Fact]
    public async Task ForeignOriginIsRejected()
    {
        var client = _factory.CreateClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/scan") { Content = JsonContent.Create(new { }) };
        req.Headers.Add("Origin", "https://evil.example");
        var res = await client.SendAsync(req);
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [Fact]
    public async Task DeviceReportsCapabilities()
    {
        var json = await _factory.CreateClient().GetFromJsonAsync<JsonElement>("/api/device");
        Assert.True(json.GetProperty("capabilities").GetProperty("back").GetBoolean());
        Assert.Equal(0, json.GetProperty("unmetRequirements").GetArrayLength());
    }
}
