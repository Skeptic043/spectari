using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Spectari.Server;
using Xunit;

namespace Spectari.Tests;

public sealed class WebServerAccessTests
{
    [Theory]
    [InlineData(false, "synthetic-current")]
    [InlineData(false, null)]
    [InlineData(true, null)]
    public async Task RoutesKeepDiscoverySeparateFromBootstrap(bool idle, string? key)
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var policy = new ViewerAccessPolicy(port, ["localhost"], () => []);
        using var server = new WebServer(port, idle ? null : new Broadcaster { StreamName = "Synthetic" },
            key, "Synthetic", policy, loopbackOnly: true);
        using var stop = new CancellationTokenSource();
        Task run = server.RunAsync(stop.Token);
        using var http = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}"), Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            using var stats = await http.GetAsync("/api/stats");
            Assert.Equal(HttpStatusCode.OK, stats.StatusCode);
            Assert.Equal("*", Assert.Single(stats.Headers.GetValues("Access-Control-Allow-Origin")));
            using var json = JsonDocument.Parse(await stats.Content.ReadAsStringAsync());
            Assert.False(json.RootElement.TryGetProperty("key", out _));

            foreach (string? origin in new[] { null, $"http://localhost:{port}", "null", "http://other.test" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, "/api/viewer-key");
                request.Headers.Add(ViewerAccessPolicy.BootstrapHeader, "1");
                if (origin is not null) request.Headers.TryAddWithoutValidation("Origin", origin);
                using var response = await http.SendAsync(request);
                bool allowed = origin is null || origin == $"http://localhost:{port}";
                Assert.Equal(allowed ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
                Assert.True(response.Headers.CacheControl?.NoStore);
                Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"));
                using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
                if (allowed) Assert.Equal(key, body.RootElement.GetProperty("key").GetString());
                else Assert.False(body.RootElement.TryGetProperty("key", out _));
            }
            using var missing = await http.GetAsync("/api/viewer-key");
            Assert.Equal(HttpStatusCode.Forbidden, missing.StatusCode);
            using var options = await http.SendAsync(new HttpRequestMessage(HttpMethod.Options, "/api/viewer-key"));
            Assert.Equal(HttpStatusCode.MethodNotAllowed, options.StatusCode);
            Assert.False(options.Headers.Contains("Access-Control-Allow-Origin"));
            using var ws = new HttpRequestMessage(HttpMethod.Get, "/ws?k=synthetic-current");
            ws.Headers.TryAddWithoutValidation("Origin", "http://other.test");
            using var denied = await http.SendAsync(ws);
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
            using var missingKey = await http.GetAsync("/ws");
            Assert.Equal(idle ? HttpStatusCode.ServiceUnavailable : key is null ? HttpStatusCode.BadRequest
                : HttpStatusCode.Forbidden, missingKey.StatusCode);
        }
        finally { stop.Cancel(); await run; }
    }
}
