using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using X360NetDrive.Server.Http;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Tests;

public sealed class WebDavEndpointTests
{
    private static readonly DateTimeOffset Modified =
        new(2024, 1, 2, 3, 4, 6, TimeSpan.Zero);
    private const string Snapshot = "snapshot-test";
    private static readonly XNamespace Dav = "DAV:";

    [Fact]
    public async Task WebDavEndpoint_OptionsAndUnsupportedMutationMethodExposeReadonlyContract()
    {
        var root = MakeRoot();

        var options = await InvokeAsync("OPTIONS", "/", root);
        var post = await InvokeAsync("POST", "/", root);

        Assert.Equal(StatusCodes.Status204NoContent, options.StatusCode);
        Assert.Equal("OPTIONS, GET, HEAD, PROPFIND", options.Headers["Allow"]);
        Assert.Equal("1", options.Headers["DAV"]);
        Assert.Equal(StatusCodes.Status405MethodNotAllowed, post.StatusCode);
        Assert.Equal("OPTIONS, GET, HEAD, PROPFIND", post.Headers["Allow"]);
    }

    [Fact]
    public async Task WebDavEndpoint_PropfindDepthZeroReturnsEscapedCollectionAndProperties()
    {
        const string requestBody = "<D:propfind xmlns:D=\"DAV:\"><D:allprop/></D:propfind>";
        var response = await InvokeAsync("PROPFIND", "/Folder A", MakeRoot(),
            depth: "0", body: requestBody, pathBase: "/webdav");

        Assert.Equal(StatusCodes.Status207MultiStatus, response.StatusCode);
        var document = XDocument.Parse(response.Body);
        var item = Assert.Single(document.Descendants(Dav + "response"));
        Assert.Equal("/webdav/Folder%20A/", (string?)item.Element(Dav + "href"));
        var properties = item.Element(Dav + "propstat")?.Element(Dav + "prop");
        Assert.NotNull(properties);
        Assert.NotNull(properties!.Element(Dav + "resourcetype")?.Element(Dav + "collection"));
        Assert.Equal("httpd/unix-directory", (string?)properties.Element(Dav + "getcontenttype"));
        Assert.Equal("0", (string?)properties.Element(Dav + "getcontentlength"));
        Assert.Equal(Modified.ToUniversalTime().ToString("R", System.Globalization.CultureInfo.InvariantCulture),
            (string?)properties.Element(Dav + "getlastmodified"));
        Assert.Equal(WebDavEndpoint.ETag(Snapshot, "/Folder A").ToString(),
            (string?)properties.Element(Dav + "getetag"));
    }

    [Fact]
    public async Task WebDavEndpoint_PropfindDepthOneReturnsEscapedRootAndImmediateChildren()
    {
        var response = await InvokeAsync("PROPFIND", "/", MakeRoot(), depth: "1", pathBase: "/webdav");

        Assert.Equal(StatusCodes.Status207MultiStatus, response.StatusCode);
        var hrefs = XDocument.Parse(response.Body).Descendants(Dav + "href")
            .Select(element => element.Value).ToArray();
        Assert.Equal(new[]
        {
            "/webdav/",
            "/webdav/file%20%26%20one.txt",
            "/webdav/Folder%20A/"
        }, hrefs);
    }

    [Fact]
    public async Task WebDavEndpoint_PropfindSeparatesUnknownPropertiesAsNotFound()
    {
        const string requestBody = """
            <D:propfind xmlns:D="DAV:" xmlns:X="urn:missing">
              <D:prop><D:displayname/><X:unknown/></D:prop>
            </D:propfind>
            """;
        var response = await InvokeAsync("PROPFIND", "/", MakeRoot(), depth: "0", body: requestBody);

        Assert.Equal(StatusCodes.Status207MultiStatus, response.StatusCode);
        var document = XDocument.Parse(response.Body);
        var successful = document.Descendants(Dav + "propstat")
            .Single(element => (string?)element.Element(Dav + "status") == "HTTP/1.1 200 OK");
        var missing = document.Descendants(Dav + "propstat")
            .Single(element => (string?)element.Element(Dav + "status") == "HTTP/1.1 404 Not Found");
        Assert.Equal("X360", (string?)successful.Element(Dav + "prop")?.Element(Dav + "displayname"));
        Assert.NotNull(missing.Element(Dav + "prop")?.Element(XName.Get("unknown", "urn:missing")));
    }

    [Fact]
    public async Task WebDavEndpoint_PropnameReturnsPropertyNamesWithoutValues()
    {
        const string requestBody = "<D:propfind xmlns:D=\"DAV:\"><D:propname/></D:propfind>";
        var response = await InvokeAsync("PROPFIND", "/Folder A", MakeRoot(),
            depth: "0", body: requestBody);

        Assert.Equal(StatusCodes.Status207MultiStatus, response.StatusCode);
        var properties = XDocument.Parse(response.Body).Descendants(Dav + "prop").Single();
        Assert.Contains(properties.Elements(), property => property.Name == Dav + "displayname");
        Assert.Contains(properties.Elements(), property => property.Name == Dav + "resourcetype");
        Assert.All(properties.Elements(), property => Assert.Empty(property.Elements()));
        Assert.All(properties.Elements(), property => Assert.Equal("", property.Value));
    }

    [Fact]
    public async Task WebDavEndpoint_RejectsDtdAndExternalEntityDeclarations()
    {
        const string requestBody = """
            <!DOCTYPE D:propfind [<!ENTITY x SYSTEM "file:///private/should-not-be-read">]>
            <D:propfind xmlns:D="DAV:"><D:prop><D:displayname>&x;</D:displayname></D:prop></D:propfind>
            """;
        var response = await InvokeAsync("PROPFIND", "/", MakeRoot(), depth: "0", body: requestBody);

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("Malformed PROPFIND XML", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebDavEndpoint_RejectsMalformedPropfindXml()
    {
        var response = await InvokeAsync("PROPFIND", "/", MakeRoot(), depth: "0",
            body: "<D:propfind xmlns:D=\"DAV:\"><D:prop>");

        Assert.Equal(StatusCodes.Status400BadRequest, response.StatusCode);
        Assert.Contains("Malformed PROPFIND XML", response.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WebDavEndpoint_TraversalAndUnknownPathsReturnNotFound()
    {
        var root = MakeRoot();

        var traversal = await InvokeAsync("GET", "/../outside.bin", root);
        var missing = await InvokeAsync("GET", "/missing.bin", root);

        Assert.Equal(StatusCodes.Status404NotFound, traversal.StatusCode);
        Assert.Equal(StatusCodes.Status404NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task WebDavEndpoint_RequiresFiniteDepthAndRejectsUnsupportedDepth()
    {
        var root = MakeRoot();

        var missingDepth = await InvokeAsync("PROPFIND", "/", root);
        var infiniteDepth = await InvokeAsync("PROPFIND", "/", root, depth: "infinity");
        var otherDepth = await InvokeAsync("PROPFIND", "/", root, depth: "2");

        Assert.Equal(StatusCodes.Status403Forbidden, missingDepth.StatusCode);
        Assert.Equal(StatusCodes.Status403Forbidden, infiniteDepth.StatusCode);
        Assert.Equal(StatusCodes.Status400BadRequest, otherDepth.StatusCode);
    }

    [Fact]
    public async Task WebDavEndpoint_GetDirectoryReturnsChildMetadataAndHeadHasNoBody()
    {
        var root = MakeRoot();

        var get = await InvokeAsync("GET", "/", root);
        var head = await InvokeAsync("HEAD", "/", root);

        Assert.Equal(StatusCodes.Status200OK, get.StatusCode);
        Assert.Contains("\"name\":\"Folder A\"", get.Body, StringComparison.Ordinal);
        Assert.Contains("\"directory\":true", get.Body, StringComparison.Ordinal);
        Assert.Equal(StatusCodes.Status200OK, head.StatusCode);
        Assert.Equal("", head.Body);
    }

    [Fact]
    public async Task WebDavHttp_GetAndHeadExposeStableEtagsAndExpectedBytes()
    {
        var expected = Enumerable.Range(0, 23).Select(i => (byte)(i * 7)).ToArray();
        var root = MakeRoot(expected);
        await using var server = await WebDavTestServer.StartAsync(root);

        using var get = await server.Client.GetAsync("/webdav/file%20%26%20one.txt");
        var getBody = await get.Content.ReadAsByteArrayAsync();
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, "/webdav/file%20%26%20one.txt");
        using var head = await server.Client.SendAsync(headRequest);

        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        Assert.Equal(expected, getBody);
        Assert.NotNull(get.Headers.ETag);
        Assert.Equal(get.Headers.ETag, head.Headers.ETag);
        Assert.Equal(WebDavEndpoint.ETag(Snapshot, "/file & one.txt").ToString(),
            get.Headers.ETag?.ToString());
        Assert.Equal((long)expected.Length, head.Content.Headers.ContentLength);
        Assert.Empty(await head.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task WebDavHttp_GetRangeReturns206WithExactContent()
    {
        var expected = Enumerable.Range(0, 32).Select(i => (byte)(255 - i)).ToArray();
        await using var server = await WebDavTestServer.StartAsync(MakeRoot(expected));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/webdav/file%20%26%20one.txt");
        request.Headers.Range = new RangeHeaderValue(7, 14);

        using var response = await server.Client.SendAsync(request);
        var content = await response.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, response.StatusCode);
        Assert.Equal(expected[7..15], content);
        Assert.Equal(7, response.Content.Headers.ContentRange?.From);
        Assert.Equal(14, response.Content.Headers.ContentRange?.To);
        Assert.Equal((long)expected.Length, response.Content.Headers.ContentRange?.Length);
        Assert.NotNull(response.Headers.ETag);
    }

    [Fact]
    public async Task WebDavHttp_UnsatisfiableRangeReturns416()
    {
        var expected = new byte[] { 1, 2, 3, 4, 5 };
        await using var server = await WebDavTestServer.StartAsync(MakeRoot(expected));
        using var request = new HttpRequestMessage(HttpMethod.Get, "/webdav/file%20%26%20one.txt");
        request.Headers.Range = new RangeHeaderValue(expected.Length, expected.Length + 2);

        using var response = await server.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, response.StatusCode);
        Assert.Equal(expected.Length, response.Content.Headers.ContentRange?.Length);
    }

    private static VirtualDirectory MakeRoot(byte[]? fileContent = null)
    {
        var root = new VirtualDirectory("X360", Modified);
        var folder = new VirtualDirectory("Folder A", Modified);
        folder.Add(new MemoryVirtualFile("nested.txt", "nested"u8.ToArray(), Modified));
        root.Add(folder);
        root.Add(new MemoryVirtualFile("file & one.txt",
            fileContent ?? "small body"u8.ToArray(), Modified));
        return root;
    }

    private static async Task<EndpointResponse> InvokeAsync(string method, string path,
        VirtualDirectory root, string? depth = null, string? body = null, string pathBase = "")
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext
        {
            RequestServices = services
        };
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.PathBase = pathBase;
        context.Request.Body = body is null
            ? new MemoryStream()
            : new MemoryStream(Encoding.UTF8.GetBytes(body));
        if (body is not null)
        {
            context.Request.ContentLength = Encoding.UTF8.GetByteCount(body);
            context.Request.ContentType = "application/xml";
        }
        if (depth is not null)
            context.Request.Headers["Depth"] = depth;
        context.Response.Body = new MemoryStream();

        await WebDavEndpoint.HandleAsync(context, root, Snapshot);

        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, Encoding.UTF8, leaveOpen: true);
        var text = await reader.ReadToEndAsync();
        return new EndpointResponse(context.Response.StatusCode, text, context.Response.Headers);
    }

    private sealed record EndpointResponse(int StatusCode, string Body, IHeaderDictionary Headers);

    private sealed class WebDavTestServer : IAsyncDisposable
    {
        private readonly WebApplication application;
        public HttpClient Client { get; }

        private WebDavTestServer(WebApplication application, HttpClient client)
        {
            this.application = application;
            Client = client;
        }

        public static async Task<WebDavTestServer> StartAsync(VirtualDirectory root)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = "Development",
                ApplicationName = typeof(WebDavEndpoint).Assembly.FullName
            });
            builder.Logging.ClearProviders();
            builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
            var application = builder.Build();
            application.Map("/webdav", branch =>
                branch.Run(context => WebDavEndpoint.HandleAsync(context, root, Snapshot)));
            await application.StartAsync();

            var server = application.Services.GetRequiredService<IServer>();
            var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses;
            Assert.NotNull(addresses);
            var address = Assert.Single(addresses);
            return new WebDavTestServer(application, new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await application.DisposeAsync();
        }
    }
}
