using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using LabGuard.Server.ClientUpdates;

namespace LabGuard.Server.Tests;

public class PeVersionReaderTests
{
    private const int SectionRawPointer = 0x200;
    private const int SectionVirtualAddress = 0x1000;
    private const int OptionalHeaderStart = 0x58;
    private const int ResourceDirectoryEntry = OptionalHeaderStart + 96 + 2 * 8;
    private const int SectionTableStart = OptionalHeaderStart + 224;

    // A managed assembly carries a version resource on every platform, so it is the real program the platform-independent tests cut up.
    private static readonly string TestAssemblyPath = typeof(PeVersionReaderTests).Assembly.Location;

    // Programs with no separate language (.mui) file, so FileVersionInfo reads the same resource the reader does.
    public static TheoryData<string> ProgramsWithoutLanguageFiles()
    {
        var candidates = new List<string> { TestAssemblyPath, Path.Combine(AppContext.BaseDirectory, "LabGuard.Server.exe") };
        candidates.AddRange(ClientInstallers());
        return ExistingFiles(candidates);
    }

    // Windows programs. FileVersionInfo gives the strings of their .mui file, which can lag behind the program's own resource after an update.
    public static TheoryData<string> WindowsPrograms()
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return ExistingFiles([Path.Combine(system, "notepad.exe"), Path.Combine(system, "kernel32.dll"), TestAssemblyPath]);
    }

    private static TheoryData<string> ExistingFiles(IEnumerable<string> candidates)
    {
        var files = new TheoryData<string>();
        foreach (var path in candidates.Where(File.Exists))
            files.Add(path);
        return files;
    }

    // Built client installers, when the client folder sits next to the server folder and has been built.
    private static IEnumerable<string> ClientInstallers([CallerFilePath] string thisFile = "")
    {
        var clientFolder = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", "client"));
        return Directory.Exists(clientFolder)
            ? Directory.EnumerateFiles(clientFolder, "LykantraSetup*.exe", SearchOption.AllDirectories)
            : [];
    }

    private static InstallerInfo? Read(byte[] bytes) => PeVersionReader.Read(new MemoryStream(bytes));

    [Theory]
    [MemberData(nameof(ProgramsWithoutLanguageFiles))]
    public void Read_RealProgram_MatchesFileVersionInfo(string path)
    {
        var info = PeVersionReader.Read(path);

        Assert.NotNull(info);
        if (!OperatingSystem.IsWindows())
            return;
        var windows = FileVersionInfo.GetVersionInfo(path);
        Assert.Equal(windows.ProductName?.Trim(), info.ProductName);
        Assert.Equal(windows.FileVersion?.Trim(), info.FileVersion);
    }

    [Theory]
    [MemberData(nameof(WindowsPrograms))]
    public void Read_WindowsProgram_MatchesTheResourceWindowsReadsFromTheProgramItself(string path)
    {
        var info = PeVersionReader.Read(path);

        Assert.NotNull(info);
        if (!OperatingSystem.IsWindows())
            return;
        Assert.Equal(NeutralVersionResource.Read(path, "ProductName")?.Trim(), info.ProductName);
        Assert.Equal(NeutralVersionResource.Read(path, "FileVersion")?.Trim(), info.FileVersion);
    }

    [Fact]
    public void Read_TestAssembly_ReadsItsProductAndVersion()
    {
        var info = PeVersionReader.Read(TestAssemblyPath);

        Assert.NotNull(info);
        Assert.Equal("LabGuard.Server.Tests", info.ProductName);
        Assert.Equal("1.0.0.0", info.FileVersion);
    }

    [Fact]
    public void Read_MissingFile_IsNotReadable()
    {
        Assert.Null(PeVersionReader.Read(Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}.exe")));
    }

    [Fact]
    public void Read_EmptyFile_IsNotReadable()
    {
        Assert.Null(Read([]));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Read_RandomBytes_IsNotReadable(int seed)
    {
        var bytes = new byte[64 * 1024];
        new Random(seed).NextBytes(bytes);
        var startsLikeAProgram = (byte[])bytes.Clone();
        startsLikeAProgram[0] = (byte)'M';
        startsLikeAProgram[1] = (byte)'Z';

        Assert.Null(Read(bytes));
        Assert.Null(Read(startsLikeAProgram));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(0x40)]
    [InlineData(0x100)]
    [InlineData(0x200)]
    public void Read_ProgramCutBeforeItsResource_IsNotReadable(int length)
    {
        var bytes = File.ReadAllBytes(TestAssemblyPath);

        Assert.Null(Read(bytes[..length]));
    }

    [Fact]
    public void Read_ProgramCutHalfway_IsNotReadable()
    {
        var bytes = File.ReadAllBytes(TestAssemblyPath);

        Assert.Null(Read(bytes[..(bytes.Length / 2)]));
    }

    [Fact]
    public void Read_ProgramCutAnywhere_NeverThrowsAndNeverReadsSomethingElse()
    {
        var bytes = File.ReadAllBytes(TestAssemblyPath);
        var whole = Read(bytes);

        for (var length = 0; length < bytes.Length; length += 97)
        {
            var info = PeVersionReader.Read(new MemoryStream(bytes, 0, length, writable: false));
            Assert.True(info is null || info == whole, $"Cut at {length} read {info}");
        }
    }

    [Fact]
    public void Read_ProgramWithoutResources_IsNotReadable()
    {
        Assert.Null(Read(SyntheticProgram()));
    }

    [Theory]
    [InlineData(0x7FFF_FFF0)]
    [InlineData(0xFFFF_FFFF)]
    [InlineData(0x3FE)]
    public void Read_HeaderOffsetOutsideTheFile_IsNotReadable(uint peOffset)
    {
        var bytes = SyntheticProgram();
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(0x3C), peOffset);

        Assert.Null(Read(bytes));
    }

    [Theory]
    [InlineData(0x9000u)]
    [InlineData(0x13FCu)]
    [InlineData(0xFFFF_FFF0u)]
    public void Read_ResourceTableOutsideAnySection_IsNotReadable(uint resourceRva)
    {
        Assert.Null(Read(SyntheticProgram(resourceRva)));
    }

    [Theory]
    [InlineData(0x8000_0FF0u)]
    [InlineData(0xFFFF_FFFFu)]
    public void Read_ResourceEntryPointingOutsideTheFile_IsNotReadable(uint target)
    {
        var bytes = SyntheticProgram(SectionVirtualAddress);
        var directory = bytes.AsSpan(SectionRawPointer);
        BinaryPrimitives.WriteUInt16LittleEndian(directory[14..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(directory[16..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(directory[20..], target);

        Assert.Null(Read(bytes));
    }

    // The smallest PE32 file the reader accepts the headers of: one 0x200-byte section at RVA 0x1000, and a resource table at the given RVA (none when 0).
    private static byte[] SyntheticProgram(uint resourceRva = 0)
    {
        var bytes = new byte[SectionRawPointer + 0x200];
        var span = bytes.AsSpan();
        span[0] = (byte)'M';
        span[1] = (byte)'Z';
        BinaryPrimitives.WriteUInt32LittleEndian(span[0x3C..], 0x40);

        BinaryPrimitives.WriteUInt32LittleEndian(span[0x40..], 0x0000_4550);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x44..], 0x14C);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x46..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(span[0x54..], 224);

        BinaryPrimitives.WriteUInt16LittleEndian(span[OptionalHeaderStart..], 0x10B);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(OptionalHeaderStart + 92)..], 16);
        BinaryPrimitives.WriteUInt32LittleEndian(span[ResourceDirectoryEntry..], resourceRva);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(ResourceDirectoryEntry + 4)..], resourceRva == 0 ? 0u : 0x100u);

        BinaryPrimitives.WriteUInt32LittleEndian(span[(SectionTableStart + 8)..], 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(SectionTableStart + 12)..], SectionVirtualAddress);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(SectionTableStart + 16)..], 0x200);
        BinaryPrimitives.WriteUInt32LittleEndian(span[(SectionTableStart + 20)..], SectionRawPointer);
        return bytes;
    }
}

// Asks Windows for a version string from the program file itself, skipping any .mui language file (FILE_VER_GET_NEUTRAL).
internal static class NeutralVersionResource
{
    private const uint FileVerGetNeutral = 0x2;

    [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileVersionInfoSizeExW(uint flags, string fileName, out uint handle);

    [DllImport("version.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool GetFileVersionInfoExW(uint flags, string fileName, uint handle, uint length, IntPtr data);

    [DllImport("version.dll", CharSet = CharSet.Unicode)]
    private static extern bool VerQueryValueW(IntPtr block, string subBlock, out IntPtr value, out uint length);

    public static string? Read(string path, string name)
    {
        var size = GetFileVersionInfoSizeExW(FileVerGetNeutral, path, out _);
        if (size == 0)
            return null;

        var buffer = new byte[size];
        var pin = GCHandle.Alloc(buffer, GCHandleType.Pinned);
        try
        {
            var block = pin.AddrOfPinnedObject();
            if (!GetFileVersionInfoExW(FileVerGetNeutral, path, 0, size, block)
                || !VerQueryValueW(block, @"\VarFileInfo\Translation", out var translation, out var translationLength)
                || translationLength < 4)
                return null;

            var language = (ushort)Marshal.ReadInt16(translation);
            var codePage = (ushort)Marshal.ReadInt16(translation, 2);
            return VerQueryValueW(block, $@"\StringFileInfo\{language:X4}{codePage:X4}\{name}", out var value, out _)
                ? Marshal.PtrToStringUni(value)
                : null;
        }
        finally
        {
            pin.Free();
        }
    }
}
