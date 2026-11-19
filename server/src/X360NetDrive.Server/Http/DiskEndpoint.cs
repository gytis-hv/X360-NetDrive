using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;
using X360NetDrive.Server.Storage;

namespace X360NetDrive.Server.Http;

public static class DiskEndpoint
{
    public static IResult Read(VirtualFile disk, string snapshot, string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))).ToLowerInvariant()[..16];
        var tag = new EntityTagHeaderValue($"\"{snapshot}-{hash}\"");
        return Results.Stream(new VirtualReadStream(disk), "application/octet-stream",
            lastModified: disk.LastModified, entityTag: tag, enableRangeProcessing: true);
    }
}
