namespace LabGuard.Server.ClientUpdates;

// What the Windows version resource of an installer says about it. Null fields mean the resource was not readable.
public sealed record InstallerInfo(string? ProductName, string? FileVersion);

// Reads the version resource of an installer file, so the upload checks can be tested without building real PE files.
public interface IInstallerInspector
{
    // Null when the file has no readable version resource (not a PE file, or damaged).
    InstallerInfo? Read(string path);
}

// Reads the resource with a managed parser rather than FileVersionInfo, which only reads it on Windows and the server also runs on Linux.
public sealed class PeVersionInstallerInspector : IInstallerInspector
{
    public InstallerInfo? Read(string path) => PeVersionReader.Read(path);
}
