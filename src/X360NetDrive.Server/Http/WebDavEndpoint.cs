using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.Net.Http.Headers;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Http;

public static class WebDavEndpoint
{
    private static readonly XNamespace Dav = "DAV:";
    private static readonly string[] PropertyNames =
        ["displayname", "resourcetype", "getcontentlength", "getlastmodified", "creationdate", "getetag", "getcontenttype"];
    private const string Allowed = "OPTIONS, GET, HEAD, PROPFIND";

    public static EntityTagHeaderValue ETag(string snapshot, string path) =>
        new($"\"{snapshot}-{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..16]}\"");

    public static async Task HandleAsync(HttpContext context, VirtualDirectory root, string snapshot)
    {
        context.Response.Headers.Allow = Allowed;
        context.Response.Headers["DAV"] = "1";
        if (context.Request.Method == "OPTIONS")
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
        if (context.Request.Method is not ("GET" or "HEAD" or "PROPFIND"))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        var path = context.Request.Path.Value ?? "";
        var node = root.Find(path);
        if (node is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        if (context.Request.Method is "GET" or "HEAD")
        {
            if (node is VirtualFile file)
            {
                await Results.Stream(new VirtualReadStream(file), "application/octet-stream",
                    lastModified: file.LastModified, entityTag: ETag(snapshot, path),
                    enableRangeProcessing: true).ExecuteAsync(context);
            }
            else if (node is VirtualDirectory directory)
            {
                var result = directory.Children.Select(child => new
                {
                    child.Name, Directory = child is VirtualDirectory,
                    Length = child is VirtualFile childFile ? childFile.Length : 0
                });
                if (context.Request.Method == "HEAD")
                    context.Response.StatusCode = StatusCodes.Status200OK;
                else
                    await context.Response.WriteAsJsonAsync(result, context.RequestAborted);
            }
            return;
        }

        var depth = context.Request.Headers["Depth"].ToString();
        if (depth.Length == 0 || depth == "infinity")
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/xml; charset=utf-8";
            await context.Response.WriteAsync(new XElement(Dav + "error",
                new XElement(Dav + "propfind-finite-depth")).ToString(), context.RequestAborted);
            return;
        }
        if (depth is not ("0" or "1"))
        {
            await Results.Problem("Depth must be 0 or 1.", statusCode: 400).ExecuteAsync(context);
            return;
        }
        XName[] properties = PropertyNames.Select(name => Dav + name).ToArray();
        var namesOnly = false;
        if (context.Request.ContentLength is > 0 || context.Request.Headers.TransferEncoding.Count > 0)
        {
            try
            {
                using var reader = XmlReader.Create(context.Request.Body, new XmlReaderSettings
                {
                    Async = true, DtdProcessing = DtdProcessing.Prohibit,
                    XmlResolver = null, MaxCharactersInDocument = 65536
                });
                var document = await XDocument.LoadAsync(reader, LoadOptions.None, context.RequestAborted);
                if (document.Root?.Name != Dav + "propfind")
                    throw new XmlException("Expected DAV:propfind.");
                if (document.Root.Element(Dav + "prop") is { } prop)
                    properties = prop.Elements().Select(element => element.Name).ToArray();
                else if (document.Root.Element(Dav + "propname") is not null)
                    namesOnly = true;
                else if (document.Root.Element(Dav + "allprop") is null)
                    throw new XmlException("Expected prop, propname, or allprop.");
            }
            catch (XmlException error)
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>()
                    .CreateLogger("WebDAV").LogWarning(error, "Rejected malformed PROPFIND body.");
                await Results.Problem("Malformed PROPFIND XML.", statusCode: 400).ExecuteAsync(context);
                return;
            }
        }
        var responses = new List<XElement> { Response(context, node, path, properties, namesOnly, snapshot) };
        if (depth == "1" && node is VirtualDirectory collection)
        {
            foreach (var child in collection.Children.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase))
                responses.Add(Response(context, child, path.TrimEnd('/') + "/" + child.Name, properties, namesOnly, snapshot));
        }
        context.Response.StatusCode = StatusCodes.Status207MultiStatus;
        context.Response.ContentType = "application/xml; charset=utf-8";
        var xml = new XDocument(new XElement(Dav + "multistatus", new XAttribute(XNamespace.Xmlns + "D", Dav), responses));
        await context.Response.WriteAsync(xml.ToString(SaveOptions.DisableFormatting), context.RequestAborted);
    }

    private static XElement Response(HttpContext context, VirtualNode node, string path,
        XName[] properties, bool namesOnly, string snapshot)
    {
        var good = new List<XElement>();
        var missing = new List<XElement>();
        foreach (var property in properties)
        {
            if (property.Namespace != Dav || !PropertyNames.Contains(property.LocalName))
            {
                missing.Add(new XElement(property));
                continue;
            }
            object? value = property.LocalName switch
            {
                "displayname" => node.Name,
                "resourcetype" => node is VirtualDirectory ? new XElement(Dav + "collection") : null,
                "getcontentlength" => node is VirtualFile file ? file.Length : 0L,
                "getlastmodified" => node.LastModified.ToUniversalTime().ToString("R", CultureInfo.InvariantCulture),
                "creationdate" => node.LastModified.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture),
                "getetag" => ETag(snapshot, path).ToString(),
                "getcontenttype" => node is VirtualDirectory ? "httpd/unix-directory" : "application/octet-stream",
                _ => throw new InvalidOperationException("Unsupported property passed validation.")
            };
            good.Add(namesOnly ? new XElement(property) : new XElement(property, value));
        }
        var encoded = string.Join('/', path.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var href = context.Request.PathBase.Value + "/" + encoded;
        if (node is VirtualDirectory && !href.EndsWith('/'))
            href += "/";
        var response = new XElement(Dav + "response", new XElement(Dav + "href", href));
        if (good.Count > 0)
            response.Add(new XElement(Dav + "propstat", new XElement(Dav + "prop", good),
                new XElement(Dav + "status", "HTTP/1.1 200 OK")));
        if (missing.Count > 0)
            response.Add(new XElement(Dav + "propstat", new XElement(Dav + "prop", missing),
                new XElement(Dav + "status", "HTTP/1.1 404 Not Found")));
        return response;
    }
}
