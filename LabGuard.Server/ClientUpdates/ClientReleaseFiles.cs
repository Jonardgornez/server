using LabGuard.Server.Data;
using Microsoft.Data.Sqlite;

namespace LabGuard.Server.ClientUpdates;

// Where the stored installer lives: a folder next to the database, with names the server picks.
// The file is kept under <sha256>.exe, so a client's file name never reaches the disk.
public sealed class ClientReleaseFiles(string folder, ILogger<ClientReleaseFiles> logger)
{
    public const string FolderName = "client-updates";
    // Overrides the folder. Tests use it so parallel test servers don't share files.
    public const string FolderConfigurationKey = "Server:ClientUpdatesFolder";
    public const long MaxInstallerBytes = 150L * 1024 * 1024;
    // An upload temp file this old was left by a crash, not by an upload still streaming in.
    public static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);
    private const string ExeExtension = ".exe";
    private const string TempPrefix = "upload-";
    private const string TempExtension = ".tmp";

    private readonly SemaphoreSlim gate = new(1, 1);

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

    public static string FileNameFor(string sha256) => $"{sha256}{ExeExtension}";

    // The upload is written here first, in the same folder so the final move is a rename.
    public string NewTempPath()
    {
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"{TempPrefix}{Guid.NewGuid():N}{TempExtension}");
    }

    public string PathFor(string fileName) => Path.Combine(folder, fileName);

    // Runs one store or removal at a time. Each one moves or deletes files and then sweeps, so two of them must not interleave:
    // a sweep could otherwise remove the file another upload has just moved into place, before its commit.
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

    // Removes what no stored release needs: every .exe except the stored one, and every upload temp file older than the
    // stale age. A fresh temp file is never touched, because it may belong to an upload still streaming in. Null keeps no .exe.
    // Best effort, like TryDelete. Other files in the folder are left alone.
    public void SweepExcept(string? keepFileName, DateTimeOffset now)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var keep = keepFileName is null ? null : PathFor(keepFileName);
        var staleTempBefore = now.UtcDateTime - StaleTempAge;
        foreach (var path in Directory.GetFiles(folder))
        {
            var name = Path.GetFileName(path);
            if (name.EndsWith(ExeExtension, StringComparison.OrdinalIgnoreCase))
            {
                if (keep is null || !string.Equals(path, keep, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(path);
                }
            }
            else if (name.StartsWith(TempPrefix, StringComparison.OrdinalIgnoreCase)
                && name.EndsWith(TempExtension, StringComparison.OrdinalIgnoreCase)
                && File.GetLastWriteTimeUtc(path) < staleTempBefore)
            {
                TryDelete(path);
            }
        }
    }

    // Best effort: a file still open by a PC's download can't be removed on Windows, so it is logged and left.
    public void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "Couldn't remove a stored client installer. It can be deleted by hand.");
        }
    }
}
