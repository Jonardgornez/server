using LabGuard.Server.ClientUpdates;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;

namespace LabGuard.Server.Tests;

public class ClientReleaseFilesTests
{
    private static readonly string ContentRoot = Path.Combine(Path.GetTempPath(), "lykantra-content-root");

    private static string ResolveFolder(IDictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        return ClientReleaseFiles.ResolveFolder(configuration, new TestHostEnvironment(ContentRoot));
    }

    private sealed class TestHostEnvironment(string contentRoot) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "LabGuard.Server.Tests";
        public string ContentRootPath { get; set; } = contentRoot;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    [Fact]
    public void ResolveFolder_WithoutAnOverride_IsAClientUpdatesFolderNextToTheDatabase()
    {
        var folder = ResolveFolder(new Dictionary<string, string?> { ["ConnectionStrings:AppDb"] = "Data Source=data/lykantra.db" });

        Assert.Equal(Path.Combine(ContentRoot, "data", ClientReleaseFiles.FolderName), folder);
    }

    [Fact]
    public void ResolveFolder_WithAnOverride_UsesThatFolder()
    {
        var override_ = Path.Combine(Path.GetTempPath(), "lykantra-override");

        var folder = ResolveFolder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:AppDb"] = "Data Source=data/lykantra.db",
            [ClientReleaseFiles.FolderConfigurationKey] = override_,
        });

        Assert.Equal(override_, folder);
    }

    [Fact]
    public void SweepExcept_RemovesEverythingButTheKeptFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"lykantra-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "keep.exe"), "keep");
            File.WriteAllText(Path.Combine(folder, "old.exe"), "old");
            File.WriteAllText(Path.Combine(folder, "upload-x.tmp"), "tmp");

            new ClientReleaseFiles(folder, NullLogger<ClientReleaseFiles>.Instance).SweepExcept("keep.exe");

            Assert.Equal(["keep.exe"], Directory.GetFiles(folder).Select(Path.GetFileName));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SweepExcept_WithNothingToKeep_RemovesAllFiles()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"lykantra-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "old.exe"), "old");

            new ClientReleaseFiles(folder, NullLogger<ClientReleaseFiles>.Instance).SweepExcept(null);

            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void FileNameFor_IsTheHashWithAnExeExtension()
    {
        var sha256 = new string('a', 64);

        Assert.Equal($"{sha256}.exe", ClientReleaseFiles.FileNameFor(sha256));
    }
}
