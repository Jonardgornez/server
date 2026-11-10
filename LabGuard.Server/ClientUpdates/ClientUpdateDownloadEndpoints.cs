using System.Text.RegularExpressions;
using LabGuard.Server.Auth;
using LabGuard.Server.Clients;
using LabGuard.Server.Data;
using Microsoft.EntityFrameworkCore;

namespace LabGuard.Server.ClientUpdates;

// The PC's download of the stored installer. It authenticates with its token, the same way its heartbeat does.
public static partial class ClientUpdateDownloadEndpoints
{
    private const string NotOfferedMessage = "No client update is offered to this PC.";

    // \z, not $: $ also matches before a trailing newline, which would let "<hash>\n" through.
    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Sha256Pattern();

    // Streams the installer from disk, and only to the PC the release is currently offered to. Any other answer is a 404,
    // so the response never says whether a release exists.
    internal static async Task<IResult> DownloadForPcAsync(string sha256, HttpContext http, AppDbContext db, ClientReleaseFiles files)
    {
        // Checked before anything else: a malformed hash never reaches the database or the file system.
        if (!Sha256Pattern().IsMatch(sha256))
        {
            return NotOffered();
        }

        if (ClientEndpoints.ReadBearerToken(http.Request) is not { } token)
        {
            return ClientEndpoints.NotRegistered();
        }

        var ct = http.RequestAborted;
        if (await ClientEndpoints.FindPcIdAsync(db, token, ct) is not { } pcId)
        {
            return ClientEndpoints.NotRegistered();
        }

        var isOffered = await db.ClientReleases
            .AsNoTracking()
            .Where(r => r.Sha256 == sha256)
            .Join(
                db.Pcs.AsNoTracking().Where(p => p.Id == pcId),
                release => release.Version,
                pc => pc.ClientUpdateTargetVersion,
                (release, pc) => pc.Id)
            .AnyAsync(ct);
        // The path comes from the validated hash, the server's own naming, and never from anything a client sent.
        var path = files.PathFor(ClientReleaseFiles.FileNameFor(sha256));
        if (!isOffered || !File.Exists(path))
        {
            return NotOffered();
        }

        // Nothing may sniff or run the file, and no copy may be cached: it is a program that runs as SYSTEM.
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        http.Response.Headers.CacheControl = "no-store";
        return Results.File(path, "application/octet-stream");
    }

    private static IResult NotOffered() =>
        Results.Json(new ErrorResponse(NotOfferedMessage), statusCode: StatusCodes.Status404NotFound);
}
