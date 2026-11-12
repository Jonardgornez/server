using System.Buffers.Binary;
using System.Text;

namespace LabGuard.Server.ClientUpdates;

// Reads ProductName and FileVersion from the version resource of a Windows program (PE32 or PE32+) without Windows APIs,
// so an uploaded installer can be checked on any platform. The file is untrusted: every offset is checked, only the
// headers and the version resource are read, and anything malformed gives null instead of an exception.
public static class PeVersionReader
{
    private const int MaxHeaderOffset = 64 * 1024;
    private const int MaxSections = 96;
    private const int MaxDirectoryEntries = 4096;
    private const int MaxBlocksVisited = 4096;
    private const int MaxBytesRead = 4 * 1024 * 1024;
    private const ushort Pe32Magic = 0x10b;
    private const ushort Pe32PlusMagic = 0x20b;
    private const int ResourceDirectoryIndex = 2;
    private const uint VersionResourceType = 16;
    private const uint HighBit = 0x8000_0000;

    private sealed record Section(uint VirtualAddress, uint RawSize, uint RawPointer);

    // One block of the version resource: its key, where its value starts and where its children start and end.
    private readonly record struct VersionBlock(string Key, int ValueLength, int ValueStart, int ChildrenStart, int End);

    private sealed class MalformedPeException : Exception;

    public static InstallerInfo? Read(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Read(stream);
        }
        catch (Exception)
        {
            // A file that can't be opened can't be identified, whatever the reason.
            return null;
        }
    }

    public static InstallerInfo? Read(Stream stream)
    {
        try
        {
            var reader = new BoundedReader(stream);
            var sections = ReadSections(reader, out var resourceRva);
            var versionResource = ReadVersionResource(reader, sections, resourceRva);
            return ReadStrings(versionResource);
        }
        catch (Exception)
        {
            // Malformed input of any kind means the file can't be identified; an upload must never crash the request.
            return null;
        }
    }

    private static List<Section> ReadSections(BoundedReader reader, out uint resourceRva)
    {
        var dosHeader = reader.ReadAt(0, 64);
        if (dosHeader[0] != 'M' || dosHeader[1] != 'Z')
            throw new MalformedPeException();

        var peOffset = BinaryPrimitives.ReadUInt32LittleEndian(dosHeader.AsSpan(0x3C));
        if (peOffset > MaxHeaderOffset)
            throw new MalformedPeException();

        var fileHeader = reader.ReadAt(peOffset, 24);
        if (BinaryPrimitives.ReadUInt32LittleEndian(fileHeader) != 0x0000_4550)
            throw new MalformedPeException();

        var sectionCount = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader.AsSpan(6));
        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(fileHeader.AsSpan(20));
        if (sectionCount == 0 || sectionCount > MaxSections)
            throw new MalformedPeException();

        var optionalHeader = reader.ReadAt(peOffset + 24, optionalHeaderSize);
        resourceRva = ReadResourceRva(optionalHeader);

        var sectionTable = reader.ReadAt(peOffset + 24 + optionalHeaderSize, sectionCount * 40);
        var sections = new List<Section>(sectionCount);
        for (var i = 0; i < sectionCount; i++)
        {
            var header = sectionTable.AsSpan(i * 40, 40);
            sections.Add(new Section(
                VirtualAddress: BinaryPrimitives.ReadUInt32LittleEndian(header[12..]),
                RawSize: BinaryPrimitives.ReadUInt32LittleEndian(header[16..]),
                RawPointer: BinaryPrimitives.ReadUInt32LittleEndian(header[20..])));
        }
        return sections;
    }

    private static uint ReadResourceRva(byte[] optionalHeader)
    {
        if (optionalHeader.Length < 2)
            throw new MalformedPeException();

        var directoriesStart = BinaryPrimitives.ReadUInt16LittleEndian(optionalHeader) switch
        {
            Pe32Magic => 96,
            Pe32PlusMagic => 112,
            _ => throw new MalformedPeException(),
        };
        var directoryCountOffset = directoriesStart - 4;
        var resourceEntryOffset = directoriesStart + ResourceDirectoryIndex * 8;
        if (optionalHeader.Length < resourceEntryOffset + 8)
            throw new MalformedPeException();

        var directoryCount = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(directoryCountOffset));
        if (directoryCount <= ResourceDirectoryIndex)
            throw new MalformedPeException();

        var rva = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(resourceEntryOffset));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(optionalHeader.AsSpan(resourceEntryOffset + 4));
        if (rva == 0 || size == 0)
            throw new MalformedPeException();
        return rva;
    }

    // Follows the resource tree: the version type, then its first name, then that name's first language.
    private static byte[] ReadVersionResource(BoundedReader reader, List<Section> sections, uint resourceRva)
    {
        var typeEntry = FindEntry(reader, sections, resourceRva, resourceRva, entry => entry.Name == VersionResourceType && entry.IsDirectory);
        var nameEntry = FindEntry(reader, sections, resourceRva, Offset(resourceRva, typeEntry.Target), entry => entry.IsDirectory);
        var languageEntry = FindEntry(reader, sections, resourceRva, Offset(resourceRva, nameEntry.Target), entry => !entry.IsDirectory);

        var dataEntry = ReadAtRva(reader, sections, Offset(resourceRva, languageEntry.Target), 16);
        var dataRva = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry);
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(dataEntry.AsSpan(4));
        // The root block's length is a 16-bit field, so a real version resource never needs more than 64 KB.
        var length = (int)Math.Min(dataSize, ushort.MaxValue);
        if (length < 6)
            throw new MalformedPeException();
        return ReadAtRva(reader, sections, dataRva, length);
    }

    private readonly record struct DirectoryEntry(uint Name, uint Target)
    {
        public bool IsDirectory => (Target & HighBit) != 0;
    }

    private static DirectoryEntry FindEntry(BoundedReader reader, List<Section> sections, uint resourceRva, uint directoryRva, Func<DirectoryEntry, bool> matches)
    {
        var header = ReadAtRva(reader, sections, directoryRva, 16);
        var count = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(12)) + BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(14));
        if (count == 0 || count > MaxDirectoryEntries)
            throw new MalformedPeException();

        var entries = ReadAtRva(reader, sections, Offset(directoryRva, 16), count * 8);
        for (var i = 0; i < count; i++)
        {
            var entry = new DirectoryEntry(
                BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8)),
                BinaryPrimitives.ReadUInt32LittleEndian(entries.AsSpan(i * 8 + 4)));
            if (matches(entry))
                return entry;
        }
        throw new MalformedPeException();
    }

    // Entry targets are offsets from the start of the resource section; the high bit only marks a subdirectory.
    private static uint Offset(uint baseRva, uint offset)
    {
        var sum = (ulong)baseRva + (offset & ~HighBit);
        if (sum > uint.MaxValue)
            throw new MalformedPeException();
        return (uint)sum;
    }

    private static byte[] ReadAtRva(BoundedReader reader, List<Section> sections, uint rva, int length)
    {
        foreach (var section in sections)
        {
            if (rva < section.VirtualAddress)
                continue;
            var offsetInSection = (ulong)(rva - section.VirtualAddress);
            if (offsetInSection + (ulong)length > section.RawSize)
                continue;
            return reader.ReadAt((long)section.RawPointer + (long)offsetInSection, length);
        }
        throw new MalformedPeException();
    }

    private static InstallerInfo ReadStrings(byte[] resource)
    {
        var budget = MaxBlocksVisited;
        var root = ReadBlock(resource, 0, resource.Length);
        if (root.Key != "VS_VERSION_INFO")
            throw new MalformedPeException();

        foreach (var fileInfo in Children(resource, root, valueIsText: false, ref budget))
        {
            if (fileInfo.Key != "StringFileInfo")
                continue;
            foreach (var table in Children(resource, fileInfo, valueIsText: false, ref budget))
            {
                string? productName = null;
                string? fileVersion = null;
                foreach (var entry in Children(resource, table, valueIsText: true, ref budget))
                {
                    if (entry.Key == "ProductName")
                        productName ??= ReadText(resource, entry);
                    else if (entry.Key == "FileVersion")
                        fileVersion ??= ReadText(resource, entry);
                }
                // The first table is the one a single-language resource has, and what Windows reports for it.
                return new InstallerInfo(productName?.Trim(), fileVersion?.Trim());
            }
        }
        return new InstallerInfo(null, null);
    }

    // The child blocks of a block. Collected into a list so the walk can't loop forever on a crafted length.
    private static List<VersionBlock> Children(byte[] resource, VersionBlock parent, bool valueIsText, ref int budget)
    {
        var children = new List<VersionBlock>();
        var position = parent.ChildrenStart;
        while (position + 6 <= parent.End)
        {
            if (--budget < 0)
                throw new MalformedPeException();
            var child = ReadBlock(resource, position, parent.End, valueIsText);
            children.Add(child);
            position = Align(child.End);
        }
        return children;
    }

    // A block is: length, value length, type, a null-terminated UTF-16 key, padding, the value, padding, then children.
    private static VersionBlock ReadBlock(byte[] resource, int start, int parentEnd, bool valueIsText = false)
    {
        if (start < 0 || start + 6 > parentEnd)
            throw new MalformedPeException();

        var length = BinaryPrimitives.ReadUInt16LittleEndian(resource.AsSpan(start));
        var valueLength = BinaryPrimitives.ReadUInt16LittleEndian(resource.AsSpan(start + 2));
        var end = start + length;
        if (length < 6 || end > parentEnd)
            throw new MalformedPeException();

        var keyStart = start + 6;
        var keyEnd = FindTerminator(resource, keyStart, end);
        var key = Encoding.Unicode.GetString(resource, keyStart, keyEnd - keyStart);

        var valueStart = Math.Min(Align(keyEnd + 2), end);
        var valueBytes = valueIsText ? valueLength * 2 : valueLength;
        var childrenStart = Math.Min(Align(valueStart + valueBytes), end);
        return new VersionBlock(key, valueLength, valueStart, childrenStart, end);
    }

    // A string value runs to the first null or the end of its block, whichever comes first; some files count its length in bytes rather than characters.
    private static string ReadText(byte[] resource, VersionBlock entry)
    {
        if (entry.ValueLength == 0 || entry.ValueStart >= entry.End)
            return string.Empty;
        var textEnd = FindTerminator(resource, entry.ValueStart, entry.End, requireTerminator: false);
        return Encoding.Unicode.GetString(resource, entry.ValueStart, textEnd - entry.ValueStart);
    }

    private static int FindTerminator(byte[] resource, int start, int end, bool requireTerminator = true)
    {
        var position = start;
        while (position + 2 <= end)
        {
            if (resource[position] == 0 && resource[position + 1] == 0)
                return position;
            position += 2;
        }
        if (requireTerminator)
            throw new MalformedPeException();
        return position;
    }

    private static int Align(int offset) => (offset + 3) & ~3;

    // Reads exact ranges of the stream, refusing anything past its end and stopping once a fixed total has been read.
    private sealed class BoundedReader(Stream stream)
    {
        private readonly long _length = stream.Length;
        private long _bytesRead;

        public byte[] ReadAt(long offset, int count)
        {
            if (offset < 0 || count < 0 || offset + count > _length)
                throw new MalformedPeException();
            _bytesRead += count;
            if (_bytesRead > MaxBytesRead)
                throw new MalformedPeException();

            var buffer = new byte[count];
            stream.Seek(offset, SeekOrigin.Begin);
            stream.ReadExactly(buffer);
            return buffer;
        }
    }
}
