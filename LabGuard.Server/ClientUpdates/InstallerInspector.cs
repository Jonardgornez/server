using System.Diagnostics;

namespace LabGuard.Server.ClientUpdates;

// What the Windows version resource of an installer says about it. Null fields mean the resource was not readable.
public sealed record InstallerInfo(string? ProductName, string? FileVersion);

// Reads the version resource of an installer file, so the upload checks can be tested without building real PE files.
public interface IInstallerInspector
{
    // Null when the file has no readable version resource (not a PE file, or damaged).
    InstallerInfo? Read(string path);
}

public sealed class FileVersionInstallerInspector : IInstallerInspector
{
    public InstallerInfo? Read(string path)
    {
        try
        {
            var info = FileVersionInfo.GetVersionInfo(path);
            // The version resource pads its strings with spaces, so they are trimmed before anything compares them.
            return new InstallerInfo(info.ProductName?.Trim(), info.FileVersion?.Trim());
        }
        catch (Exception)
        {
            // Any failure to read the resource means the file can't be identified, whatever the exception type.
            return null;
        }
    }
}
