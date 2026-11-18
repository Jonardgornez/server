using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace LabGuard.Server.SharedFiles;

// How much free space the disk holding a folder has. Tests replace it to simulate a full disk.
public interface IDiskSpace
{
    long AvailableBytes(string folder);
}

public sealed class DriveDiskSpace : IDiskSpace
{
    public long AvailableBytes(string folder) =>
        new DriveInfo(Path.GetPathRoot(Path.GetFullPath(folder))!).AvailableFreeSpace;
}

// Where the shared files live: a folder next to the database. Each file is kept under its SHA-256 alone, so nothing the
// administrator or a PC sends ever becomes part of a path on disk.
public sealed partial class SharedFileStorage(string folder, IDiskSpace diskSpace, ILogger<SharedFileStorage> logger)
{
    public const string FolderName = "shared-files";
    // Overrides the folder. Tests use it so parallel test servers don't share files.
    public const string FolderConfigurationKey = "Server:SharedFilesFolder";
    // An upload is refused when it would leave the server with less free space than this.
    public const long MinFreeBytes = 1024L * 1024 * 1024;
    // An upload temp file not written to for this long was left by a crash or a dropped upload.
    public static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);
    private const string TempPrefix = "upload-";
    private const string TempExtension = ".tmp";

    private readonly SemaphoreSlim gate = new(1, 1);

    // \z, not $: $ also matches before a trailing newline.
    [GeneratedRegex(@"^[0-9a-f]{64}\z")]
    private static partial Regex Sha256Pattern();

    public string Folder => folder;

    public static string ResolveFolder(IConfiguration configuration, IHostEnvironment environment)
    {
        var configured = configuration[FolderConfigurationKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured, environment.ContentRootPath);
        }

        var connection = new SqliteConnectionStringBuilder(configuration.GetConnectionString("AppDb"));
        var databasePath = Path.GetFullPath(connection.DataSource, environment.ContentRootPath);
        return Path.Combine(Path.GetDirectoryName(databasePath)!, FolderName);
    }

    public static bool IsSha256(string value) => Sha256Pattern().IsMatch(value);

    // The upload is written here first, in the same folder so the final move is a rename.
    public string NewTempPath()
    {
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{TempPrefix}{Guid.NewGuid():N}{TempExtension}");
    }

    // The stored file for a hash. Refuses anything that isn't a lowercase SHA-256, so no other path can be built.
    public string PathFor(string sha256) =>
        IsSha256(sha256) ? Path.Combine(folder, sha256) : throw new ArgumentException("Not a SHA-256 hash.", nameof(sha256));

    // True while writing this many more bytes still leaves the minimum free space.
    public bool HasRoomFor(long moreBytes)
    {
        Directory.CreateDirectory(folder);
        return diskSpace.AvailableBytes(folder) - moreBytes >= MinFreeBytes;
    }

    // Runs one store or removal at a time, so a removal can't delete a file another upload has just moved into place or reused.
    public async Task<T> ExclusiveAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await action();
        }
        finally
        {
            gate.Release();
        }
    }

    // Removes upload temp files that have not been written to for the stale age. A fresh one may belong to an upload still streaming in.
    public void SweepStaleTemps(DateTimeOffset now)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var staleBefore = now.UtcDateTime - StaleTempAge;
        foreach (var path in Directory.GetFiles(folder, $"{TempPrefix}*{TempExtension}"))
        {
            if (File.GetLastWriteTimeUtc(path) < staleBefore)
            {
                TryDelete(path);
            }
        }
    }

    // The stored files: every file named by a SHA-256, with its hash and when it was last written. Temp files are not listed.
    public IReadOnlyList<(string Sha256, DateTime LastWriteUtc)> StoredFiles()
    {
        if (!Directory.Exists(folder))
        {
            return [];
        }

        return Directory.GetFiles(folder)
            .Select(path => (Sha256: Path.GetFileName(path), Path: path))
            .Where(file => IsSha256(file.Sha256))
            .Select(file => (file.Sha256, File.GetLastWriteTimeUtc(file.Path)))
            .ToList();
    }

    // Best effort: a file still open by a PC's download can't be removed on Windows, so it is logged and left.
    // True only when a file was there and is now removed.
    public bool TryDelete(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            File.Delete(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Couldn't remove a shared file. It can be deleted by hand.");
            return false;
        }
    }
}
