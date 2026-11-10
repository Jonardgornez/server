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

    public static string FileNameFor(string sha256) => $"{sha256}.exe";

    // The upload is written here first, in the same folder so the final move is a rename.
    public string NewTempPath()
    {
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, $"upload-{Guid.NewGuid():N}.tmp");
    }

    public string PathFor(string fileName) => Path.Combine(folder, fileName);

    // Removes every file in the folder except the stored release, so a file left by a crash or a failed delete doesn't stay on disk.
    // Null keeps nothing. Best effort, like TryDelete.
    public void SweepExcept(string? keepFileName)
    {
        if (!Directory.Exists(folder))
        {
            return;
        }

        var keep = keepFileName is null ? null : PathFor(keepFileName);
        foreach (var path in Directory.GetFiles(folder))
        {
            if (keep is null || !string.Equals(path, keep, StringComparison.OrdinalIgnoreCase))
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
