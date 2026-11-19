using System.Net;
using System.Net.Http.Headers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using X360NetDrive.Server.Http;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

public sealed class DiskEndpointTests
{
    [Fact]
    public async Task DiskEndpoint_HeadRangeAndUnsatisfiableRangePreserveHttpContract()
    {
        var bytes = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        var file = new MemoryVirtualFile("disk", bytes, new DateTimeOffset(2024, 1, 2, 0, 0, 0, TimeSpan.Zero));
        const string path = "/exports/0123456789abcdef01234567/disk";
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        await using var app = builder.Build();
        app.MapMethods(path, ["GET", "HEAD"], () => DiskEndpoint.Read(file, "0123456789abcdef01234567", path));
        await app.StartAsync();
        var address = Assert.Single(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses);
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        using var get = await client.GetAsync(path);
        using var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Assert.Equal(bytes, await get.Content.ReadAsByteArrayAsync());
        Assert.Equal(get.Headers.ETag, head.Headers.ETag);
        Assert.NotNull(head.Headers.ETag);
        Assert.Equal("\"0123456789abcdef01234567-4760420012de2f30\"", head.Headers.ETag!.ToString());
        Assert.Equal(bytes.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());

        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, path);
        rangeRequest.Headers.Range = new RangeHeaderValue(7, 14);
        using var range = await client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(bytes[7..15], await range.Content.ReadAsByteArrayAsync());
        Assert.Equal(7, range.Content.Headers.ContentRange?.From);
        Assert.Equal(14, range.Content.Headers.ContentRange?.To);
        Assert.Equal(bytes.Length, range.Content.Headers.ContentRange?.Length);
        Assert.Equal(get.Headers.ETag, range.Headers.ETag);

        using var invalidRequest = new HttpRequestMessage(HttpMethod.Get, path);
        invalidRequest.Headers.Range = new RangeHeaderValue(bytes.Length, bytes.Length + 2);
        using var invalid = await client.SendAsync(invalidRequest);
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, invalid.StatusCode);
        Assert.Equal(bytes.Length, invalid.Content.Headers.ContentRange?.Length);
    }
}
