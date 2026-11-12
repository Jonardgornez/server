namespace LabGuard.Server.Network;

// One-off command for moving a Windows server into the Linux container: `--export-pc-channel <destination>` writes
// the PC channel identity from the DPAPI file this server uses into the portable format, so PCs stay paired.
// It runs instead of the server and never creates an identity.
public static class PcChannelExportCommand
{
    public const string Flag = "--export-pc-channel";

    public static bool IsRequested(string[] args) => args.Contains(Flag);

    // Returns the process exit code.
    public static int Run(string[] args)
    {
        var index = Array.IndexOf(args, Flag);
        if (index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            Console.Error.WriteLine($"Usage: {Flag} <destination file>");
            return 2;
        }

        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("The PC channel export reads the Windows DPAPI file, so run it on the Windows server.");
            return 1;
        }

        var destination = Path.GetFullPath(args[index + 1]);
        // The source path is resolved exactly as the server resolves it, from the remaining arguments and configuration.
        var builder = WebApplication.CreateBuilder(args.Where((_, i) => i != index && i != index + 1).ToArray());
        var source = PcChannelStore.ResolvePath(builder.Configuration, builder.Environment.ContentRootPath);

        try
        {
            PcChannelStore.ExportPortable(source, destination);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Export failed: {exception.Message}");
            return 1;
        }

        Console.WriteLine(
            $"PC channel identity exported to '{destination}'. It holds the private key unencrypted: " +
            "delete it as soon as it has been imported on the new server.");
        return 0;
    }
}
