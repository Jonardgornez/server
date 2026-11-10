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

    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    // Runs a sweep over a folder holding the given files, each with its last write time, and returns what is left.
    private static string[] SweptFiles(string keepFileName, IReadOnlyDictionary<string, DateTimeOffset> files)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"lykantra-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            foreach (var (name, written) in files)
            {
                var path = Path.Combine(folder, name);
                File.WriteAllText(path, name);
                File.SetLastWriteTimeUtc(path, written.UtcDateTime);
            }

            new ClientReleaseFiles(folder, NullLogger<ClientReleaseFiles>.Instance).SweepExcept(keepFileName, Now);

            return Directory.GetFiles(folder).Select(Path.GetFileName).Select(name => name!).Order().ToArray();
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SweepExcept_RemovesOtherInstallers_AndKeepsTheStoredOne()
    {
        var left = SweptFiles("keep.exe", new Dictionary<string, DateTimeOffset>
        {
            ["keep.exe"] = Now,
            ["old.exe"] = Now,
        });

        Assert.Equal(["keep.exe"], left);
    }

    [Fact]
    public void SweepExcept_WithNothingToKeep_RemovesEveryInstaller()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"lykantra-sweep-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(Path.Combine(folder, "old.exe"), "old");

            new ClientReleaseFiles(folder, NullLogger<ClientReleaseFiles>.Instance).SweepExcept(null, Now);

            Assert.Empty(Directory.GetFiles(folder));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void SweepExcept_KeepsAFreshUploadTemp_WhichMayStillBeStreamingIn()
    {
        var left = SweptFiles("keep.exe", new Dictionary<string, DateTimeOffset>
        {
            ["keep.exe"] = Now,
            ["upload-inflight.tmp"] = Now.AddMinutes(-5),
        });

        Assert.Equal(["keep.exe", "upload-inflight.tmp"], left);
    }

    [Fact]
    public void SweepExcept_RemovesAnUploadTempOlderThanAnHour()
    {
        var left = SweptFiles("keep.exe", new Dictionary<string, DateTimeOffset>
        {
            ["keep.exe"] = Now,
            ["upload-crashed.tmp"] = Now.Subtract(ClientReleaseFiles.StaleTempAge).AddMinutes(-1),
        });

        Assert.Equal(["keep.exe"], left);
    }

    [Fact]
    public void SweepExcept_LeavesFilesItDoesNotOwn()
    {
        var left = SweptFiles("keep.exe", new Dictionary<string, DateTimeOffset>
        {
            ["keep.exe"] = Now,
            ["notes.txt"] = Now.AddDays(-2),
        });

        Assert.Equal(["keep.exe", "notes.txt"], left);
    }

    [Fact]
    public void FileNameFor_IsTheHashWithAnExeExtension()
    {
        var sha256 = new string('a', 64);

        Assert.Equal($"{sha256}.exe", ClientReleaseFiles.FileNameFor(sha256));
    }
}
