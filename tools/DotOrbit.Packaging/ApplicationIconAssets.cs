using System.Buffers.Binary;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace DotOrbit.Packaging;

internal static class ApplicationIconAssets
{
    private const string ContractError = "application-icon-contract-invalid";
    private const string ManifestRelativePath = "assets/orbit-mark.native-icon.json";
    private const string ProjectRelativePath = "src/DotOrbit.Desktop/DotOrbit.Desktop.csproj";
    private const string ExpectedIconProjectPath = "Assets/dot-orbit.ico";
    private static readonly int[] RequiredSizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
    private static readonly byte[] PngSignature = [137, 80, 78, 71, 13, 10, 26, 10];
    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    internal static void Validate(string repositoryRoot)
    {
        try
        {
            var manifestPath = Path.Combine(repositoryRoot, ManifestRelativePath);
            var manifest = JsonSerializer.Deserialize<IconAssetManifest>(
                File.ReadAllText(manifestPath),
                ManifestJsonOptions);
            if (manifest is null
                || manifest.SchemaVersion != 1
                || manifest.Sizes is null
                || string.IsNullOrWhiteSpace(manifest.SourcePath)
                || string.IsNullOrWhiteSpace(manifest.SourceSha256)
                || string.IsNullOrWhiteSpace(manifest.OutputPath)
                || string.IsNullOrWhiteSpace(manifest.OutputSha256)
                || !string.Equals(manifest.SourcePath, "assets/orbit-mark.svg", StringComparison.Ordinal)
                || !string.Equals(
                    manifest.OutputPath,
                    "src/DotOrbit.Desktop/Assets/dot-orbit.ico",
                    StringComparison.Ordinal)
                || !manifest.Sizes.SequenceEqual(RequiredSizes))
            {
                throw new PackageException(ContractError);
            }

            var sourcePath = Path.Combine(repositoryRoot, manifest.SourcePath);
            var iconPath = Path.Combine(repositoryRoot, manifest.OutputPath);
            if (!File.Exists(sourcePath)
                || !File.Exists(iconPath)
                || !NormalizedTextHashMatches(sourcePath, manifest.SourceSha256)
                || !HashMatches(iconPath, manifest.OutputSha256))
            {
                throw new PackageException(ContractError);
            }

            ValidateIconFile(iconPath);
            ValidateProjectMetadata(Path.Combine(repositoryRoot, ProjectRelativePath));
        }
        catch (PackageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidOperationException
            or XmlException)
        {
            throw new PackageException(ContractError);
        }
    }

    internal static void ValidateWindowsExecutable(string executablePath)
    {
        try
        {
            using var stream = File.OpenRead(executablePath);
            using var pe = new PEReader(stream);
            var resourceDirectory = pe.PEHeaders.PEHeader?.ResourceTableDirectory
                ?? throw new PackageException("windows-executable-icon-invalid");
            if (resourceDirectory.RelativeVirtualAddress == 0)
            {
                throw new PackageException("windows-executable-icon-invalid");
            }

            var resources = pe.GetSectionData(resourceDirectory.RelativeVirtualAddress)
                .GetContent()
                .AsSpan();
            var iconType = FindEntry(resources, 0, 3);
            var groupType = FindEntry(resources, 0, 14);
            if (iconType is null
                || groupType is null
                || !iconType.Value.IsDirectory
                || !groupType.Value.IsDirectory
                || DirectoryEntryCount(resources, iconType.Value.Offset) < RequiredSizes.Length)
            {
                throw new PackageException("windows-executable-icon-invalid");
            }

            var groupData = ReadFirstResource(pe, resources, groupType.Value.Offset);
            ValidateGroupIcon(groupData);
        }
        catch (PackageException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or BadImageFormatException
            or ArgumentOutOfRangeException)
        {
            throw new PackageException("windows-executable-icon-invalid");
        }
    }

    private static bool HashMatches(string path, string expected) =>
        string.Equals(
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
            expected,
            StringComparison.OrdinalIgnoreCase);

    private static bool NormalizedTextHashMatches(string path, string expected)
    {
        var bytes = File.ReadAllBytes(path);
        var normalized = new byte[bytes.Length];
        var writeIndex = 0;
        for (var readIndex = 0; readIndex < bytes.Length; readIndex++)
        {
            if (bytes[readIndex] == '\r')
            {
                normalized[writeIndex++] = (byte)'\n';
                if (readIndex + 1 < bytes.Length && bytes[readIndex + 1] == '\n')
                {
                    readIndex++;
                }
            }
            else
            {
                normalized[writeIndex++] = bytes[readIndex];
            }
        }

        return string.Equals(
            Convert.ToHexString(SHA256.HashData(normalized.AsSpan(0, writeIndex))),
            expected,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateProjectMetadata(string projectPath)
    {
        var project = XDocument.Load(projectPath);
        var applicationIcons = project.Descendants()
            .Where(element => element.Name.LocalName == "ApplicationIcon")
            .Select(element => element.Value.Trim().Replace('\\', '/'))
            .ToArray();
        var defaultWindowIconFlags = project.Descendants()
            .Where(element => element.Name.LocalName == "AvaloniaIncludeApplicationIconAsWindowIcon")
            .Select(element => element.Value.Trim())
            .ToArray();
        if (applicationIcons is not [ExpectedIconProjectPath]
            || defaultWindowIconFlags is not ["true"])
        {
            throw new PackageException(ContractError);
        }
    }

    internal static void ValidateIconFile(string iconPath)
    {
        using var stream = File.OpenRead(iconPath);
        using var reader = new BinaryReader(stream);
        if (reader.ReadUInt16() != 0
            || reader.ReadUInt16() != 1
            || reader.ReadUInt16() != RequiredSizes.Length)
        {
            throw new PackageException(ContractError);
        }

        var sizes = new HashSet<int>();
        for (var index = 0; index < RequiredSizes.Length; index++)
        {
            var widthByte = reader.ReadByte();
            var heightByte = reader.ReadByte();
            var width = widthByte == 0 ? 256 : widthByte;
            var height = heightByte == 0 ? 256 : heightByte;
            var colourCount = reader.ReadByte();
            var reserved = reader.ReadByte();
            var planes = reader.ReadUInt16();
            var bitsPerPixel = reader.ReadUInt16();
            var imageLength = reader.ReadUInt32();
            var imageOffset = reader.ReadUInt32();
            if (width != height
                || colourCount != 0
                || reserved != 0
                || planes != 1
                || bitsPerPixel != 32
                || imageLength < 24
                || imageOffset > stream.Length
                || imageLength > stream.Length - imageOffset
                || !sizes.Add(width))
            {
                throw new PackageException(ContractError);
            }

            var directoryPosition = stream.Position;
            stream.Position = imageOffset;
            var header = reader.ReadBytes(24);
            if (!header.AsSpan(0, PngSignature.Length).SequenceEqual(PngSignature)
                || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(16, 4)) != width
                || BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(20, 4)) != height)
            {
                throw new PackageException(ContractError);
            }
            stream.Position = directoryPosition;
        }

        if (!sizes.SetEquals(RequiredSizes))
        {
            throw new PackageException(ContractError);
        }
    }

    private static ResourceEntry? FindEntry(
        ReadOnlySpan<byte> resources,
        int directoryOffset,
        ushort id)
    {
        ValidateDirectoryBounds(resources, directoryOffset);
        var namedCount = BinaryPrimitives.ReadUInt16LittleEndian(resources.Slice(directoryOffset + 12, 2));
        var idCount = BinaryPrimitives.ReadUInt16LittleEndian(resources.Slice(directoryOffset + 14, 2));
        var entryOffset = directoryOffset + 16 + namedCount * 8;
        for (var index = 0; index < idCount; index++)
        {
            var current = entryOffset + index * 8;
            if (current < 0 || current > resources.Length - 8)
            {
                throw new PackageException("windows-executable-icon-invalid");
            }

            var entryId = BinaryPrimitives.ReadUInt32LittleEndian(resources.Slice(current, 4));
            if ((entryId & 0x80000000) == 0 && (entryId & 0xffff) == id)
            {
                var child = BinaryPrimitives.ReadUInt32LittleEndian(resources.Slice(current + 4, 4));
                return new ResourceEntry(
                    checked((int)(child & 0x7fffffff)),
                    (child & 0x80000000) != 0);
            }
        }

        return null;
    }

    private static int DirectoryEntryCount(ReadOnlySpan<byte> resources, int directoryOffset)
    {
        ValidateDirectoryBounds(resources, directoryOffset);
        return BinaryPrimitives.ReadUInt16LittleEndian(resources.Slice(directoryOffset + 12, 2))
            + BinaryPrimitives.ReadUInt16LittleEndian(resources.Slice(directoryOffset + 14, 2));
    }

    private static byte[] ReadFirstResource(
        PEReader pe,
        ReadOnlySpan<byte> resources,
        int typeDirectoryOffset)
    {
        var languageDirectory = ReadFirstEntry(resources, typeDirectoryOffset, expectedDirectory: true);
        var dataEntry = ReadFirstEntry(resources, languageDirectory.Offset, expectedDirectory: false);
        if (dataEntry.Offset < 0 || dataEntry.Offset > resources.Length - 16)
        {
            throw new PackageException("windows-executable-icon-invalid");
        }

        var dataRva = BinaryPrimitives.ReadUInt32LittleEndian(resources.Slice(dataEntry.Offset, 4));
        var dataLength = BinaryPrimitives.ReadUInt32LittleEndian(resources.Slice(dataEntry.Offset + 4, 4));
        return pe.GetSectionData(checked((int)dataRva))
            .GetContent(0, checked((int)dataLength))
            .ToArray();
    }

    private static ResourceEntry ReadFirstEntry(
        ReadOnlySpan<byte> resources,
        int directoryOffset,
        bool expectedDirectory)
    {
        if (DirectoryEntryCount(resources, directoryOffset) == 0
            || directoryOffset > resources.Length - 24)
        {
            throw new PackageException("windows-executable-icon-invalid");
        }

        var child = BinaryPrimitives.ReadUInt32LittleEndian(resources.Slice(directoryOffset + 20, 4));
        var entry = new ResourceEntry(
            checked((int)(child & 0x7fffffff)),
            (child & 0x80000000) != 0);
        if (entry.IsDirectory != expectedDirectory)
        {
            throw new PackageException("windows-executable-icon-invalid");
        }

        return entry;
    }

    private static void ValidateDirectoryBounds(ReadOnlySpan<byte> resources, int directoryOffset)
    {
        if (directoryOffset < 0 || directoryOffset > resources.Length - 16)
        {
            throw new PackageException("windows-executable-icon-invalid");
        }
    }

    private static void ValidateGroupIcon(ReadOnlySpan<byte> group)
    {
        if (group.Length < 6
            || BinaryPrimitives.ReadUInt16LittleEndian(group) != 0
            || BinaryPrimitives.ReadUInt16LittleEndian(group.Slice(2, 2)) != 1)
        {
            throw new PackageException("windows-executable-icon-invalid");
        }

        var count = BinaryPrimitives.ReadUInt16LittleEndian(group.Slice(4, 2));
        if (count != RequiredSizes.Length || group.Length < 6 + count * 14)
        {
            throw new PackageException("windows-executable-icon-invalid");
        }

        var sizes = new HashSet<int>();
        for (var index = 0; index < count; index++)
        {
            var entry = group.Slice(6 + index * 14, 14);
            var width = entry[0] == 0 ? 256 : entry[0];
            var height = entry[1] == 0 ? 256 : entry[1];
            if (width != height || !sizes.Add(width))
            {
                throw new PackageException("windows-executable-icon-invalid");
            }
        }

        if (!sizes.SetEquals(RequiredSizes))
        {
            throw new PackageException("windows-executable-icon-invalid");
        }
    }

    private sealed record IconAssetManifest(
        int SchemaVersion,
        string? SourcePath,
        string? SourceSha256,
        string? OutputPath,
        string? OutputSha256,
        int[]? Sizes);

    private readonly record struct ResourceEntry(int Offset, bool IsDirectory);
}
