using X360NetDrive.Server.Fat;
using X360NetDrive.Server.Http;
using X360NetDrive.Server.Library;
using X360NetDrive.Server.Storage;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 65536);
var cacheDirectory = builder.Configuration["cache"]
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "X360-NetDrive", "server-cache");
var includeGod = builder.Configuration.GetValue("god", false);
var app = builder.Build();
using var library = await IsoLibrary.OpenAsync(builder.Configuration["iso"], builder.Configuration["iso-root"],
    builder.Configuration["title"], includeGod, cacheDirectory, app.Logger);
var root = library.Root;
var snapshot = library.Snapshot;
var disk = new Fat32Disk(root, "X360NET", Convert.ToUInt32(snapshot[..8], 16));
var exportPath = $"/exports/{snapshot}/disk";
var singleGame = library.Games.Count == 1 ? library.Games[0] : null;
app.UseExceptionHandler(handler => handler.Run(async context =>
{
    var error = context.Features.Get<Microsoft.AspNetCore.Diagnostics.IExceptionHandlerFeature>()?.Error;
    app.Logger.LogError(error, "Virtual export request failed.");
    await Results.Problem("Virtual export read failed.", statusCode: 500).ExecuteAsync(context);
}));
app.MapGet("/health", () => Results.Ok(new
{
    Status = "ready", TitleId = singleGame?.TitleId, Games = library.Games.Count, Excluded = library.Excluded.Count
}));
app.MapGet("/", () => Results.Json(new
{
    Title = singleGame?.Title ?? "ISO library", TitleId = singleGame?.TitleId, MediaId = singleGame?.MediaId,
    WebDav = "/webdav/", Disk = exportPath, DiskBytes = disk.Length,
    LaunchPath = singleGame?.LaunchPath, GodPackage = singleGame?.GodPackage,
    Games = library.Games, Excluded = library.Excluded,
    ReadOnly = true
}));
app.MapMethods(exportPath, ["GET", "HEAD"], () =>
    Results.Stream(new VirtualReadStream(disk), "application/octet-stream",
        lastModified: disk.LastModified, entityTag: WebDavEndpoint.ETag(snapshot, exportPath),
        enableRangeProcessing: true));
app.Map("/webdav", (IApplicationBuilder branch) =>
    branch.Run(context => WebDavEndpoint.HandleAsync(context, root, snapshot)));
app.Logger.LogInformation("Ready: {Games} ISO(s), {Excluded} excluded, virtual disk {Bytes} bytes at {DiskPath}",
    library.Games.Count, library.Excluded.Count, disk.Length, exportPath);
await app.RunAsync();

public partial class Program;
