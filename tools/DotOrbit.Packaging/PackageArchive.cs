using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;

namespace DotOrbit.Packaging;

internal static class PackageArchive
{
    private const string LicenceFileName = "LICENSE.txt";
    private const string NoticesFileName = "THIRD-PARTY-NOTICES.md";
    private const string AngleLicenceFileName = "ANGLE-LICENSE.txt";
    private const string ApacheLicenceFileName = "APACHE-2.0.txt";
    private const string DotNetLicenceFileName = "DOTNET-LICENSE.txt";
    private const string DotNetNoticesFileName = "DOTNET-THIRD-PARTY-NOTICES.txt";
    private const string InterLicenceFileName = "INTER-OFL-1.1.txt";
    private const string SkiaNoticesFileName = "SKIA-HARFBUZZ-THIRD-PARTY-NOTICES.txt";
    private const string ManifestFileName = "package-manifest.json";

    private static readonly JsonSerializerOptions ManifestJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private static readonly Dictionary<string, RuntimePackage> SupportedRuntimes =
        new Dictionary<string, RuntimePackage>(StringComparer.Ordinal)
        {
            ["win-x64"] = new("dot-orbit.exe", "zip", false),
            ["linux-x64"] = new("dot-orbit", "tar.gz", true),
            ["osx-x64"] = new("dot-orbit", "tar.gz", true),
            ["osx-arm64"] = new("dot-orbit", "tar.gz", true),
        };

    public static string Build(
        string repositoryRoot,
        string runtimeIdentifier,
        string version,
        bool noRestore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeIdentifier);
        ArgumentException.ThrowIfNullOrWhiteSpace(version);

        if (!SupportedRuntimes.TryGetValue(runtimeIdentifier, out var runtime))
        {
            throw new PackageException("unsupported-runtime");
        }

        ApplicationIconAssets.Validate(repositoryRoot);

        var projectPath = Path.Combine(
            repositoryRoot,
            "src",
            "DotOrbit.Desktop",
            "DotOrbit.Desktop.csproj");
        var workRoot = Path.Combine(
            repositoryRoot,
            "artifacts",
            "package-work",
            runtimeIdentifier);
        var publishRoot = Path.Combine(workRoot, "publish");
        var stageRoot = Path.Combine(workRoot, "stage");
        var extractedRoot = Path.Combine(
            repositoryRoot,
            "artifacts",
            "package-smoke",
            runtimeIdentifier);
        var packageRoot = Path.Combine(repositoryRoot, "artifacts", "packages");
        var archivePath = Path.Combine(
            packageRoot,
            $"dot-orbit-{runtimeIdentifier}.{runtime.ArchiveExtension}");

        RecreateDirectory(workRoot);
        Directory.CreateDirectory(publishRoot);
        Directory.CreateDirectory(stageRoot);
        RecreateDirectory(extractedRoot);
        Directory.CreateDirectory(packageRoot);

        Publish(projectPath, publishRoot, runtimeIdentifier, version, noRestore);
        var executablePath = ValidatePublishedFiles(
            publishRoot,
            runtime.ExecutableName);
        if (string.Equals(runtimeIdentifier, "win-x64", StringComparison.Ordinal))
        {
            ApplicationIconAssets.ValidateWindowsExecutable(executablePath);
        }

        var payloads = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [runtime.ExecutableName] = executablePath,
            [LicenceFileName] = Path.Combine(repositoryRoot, "LICENSE"),
            [NoticesFileName] = Path.Combine(repositoryRoot, NoticesFileName),
            [AngleLicenceFileName] = Path.Combine(
                repositoryRoot,
                "licenses",
                AngleLicenceFileName),
            [ApacheLicenceFileName] = Path.Combine(
                repositoryRoot,
                "licenses",
                ApacheLicenceFileName),
            [DotNetLicenceFileName] = Path.Combine(
                repositoryRoot,
                "licenses",
                DotNetLicenceFileName),
            [DotNetNoticesFileName] = Path.Combine(
                repositoryRoot,
                "licenses",
                DotNetNoticesFileName),
            [InterLicenceFileName] = Path.Combine(
                repositoryRoot,
                "licenses",
                InterLicenceFileName),
            [SkiaNoticesFileName] = Path.Combine(
                repositoryRoot,
                "licenses",
                SkiaNoticesFileName),
        };
        foreach (var payload in payloads)
        {
            if (!File.Exists(payload.Value))
            {
                throw new PackageException($"missing-payload:{payload.Key}");
            }

            File.Copy(payload.Value, Path.Combine(stageRoot, payload.Key));
        }

        WriteManifest(stageRoot, runtimeIdentifier, version, payloads.Keys);
        if (File.Exists(archivePath))
        {
            File.Delete(archivePath);
        }

        if (runtime.UsesUnixArchive)
        {
            CreateTarGzip(stageRoot, archivePath, runtime.ExecutableName);
        }
        else
        {
            CreateZip(stageRoot, archivePath, runtime.ExecutableName);
        }

        VerifyAndExtractArchive(
            archivePath,
            extractedRoot,
            runtimeIdentifier,
            version,
            runtime.ExecutableName,
            runtime.UsesUnixArchive);
        return archivePath;
    }

    internal static string ValidatePublishedFiles(
        string publishRoot,
        string executableName)
    {
        var files = Directory.GetFiles(
            publishRoot,
            "*",
            SearchOption.AllDirectories);
        var relativePaths = files
            .Select(file => Path.GetRelativePath(publishRoot, file))
            .ToArray();
        if (!relativePaths.Contains(executableName, StringComparer.Ordinal)
            || relativePaths.Any(path =>
                !string.Equals(path, executableName, StringComparison.Ordinal)
                && (!string.Equals(
                    Path.GetExtension(path),
                    ".pdb",
                    StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        Path.GetFileName(path),
                        path,
                        StringComparison.Ordinal))))
        {
            throw new PackageException("publish-output-not-allowlisted");
        }

        return Path.Combine(publishRoot, executableName);
    }

    private static void Publish(
        string projectPath,
        string publishRoot,
        string runtimeIdentifier,
        string version,
        bool noRestore)
    {
        var startInfo = new ProcessStartInfo("dotnet")
        {
            UseShellExecute = false,
        };
        foreach (var argument in CreatePublishArguments(
            projectPath,
            publishRoot,
            runtimeIdentifier,
            version,
            noRestore))
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo)
            ?? throw new PackageException("publish-did-not-start");
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            throw new PackageException("publish-failed");
        }
    }

    internal static IReadOnlyList<string> CreatePublishArguments(
        string projectPath,
        string publishRoot,
        string runtimeIdentifier,
        string version,
        bool noRestore)
    {
        var arguments = new List<string>
        {
            "publish",
            projectPath,
            "--configuration",
            "Release",
            "--runtime",
            runtimeIdentifier,
            "--self-contained",
            "true",
            "--output",
            publishRoot,
            "-p:DotOrbitPackage=true",
            $"-p:Version={version}",
            "-p:IncludeSourceRevisionInInformationalVersion=false",
        };

        if (noRestore)
        {
            arguments.Add("--no-restore");
        }

        return arguments;
    }

    private static void WriteManifest(
        string stageRoot,
        string runtimeIdentifier,
        string version,
        IEnumerable<string> payloadNames)
    {
        var files = payloadNames
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                var path = Path.Combine(stageRoot, name);
                return new PackageFile(
                    name,
                    new FileInfo(path).Length,
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                        .ToLowerInvariant());
            })
            .ToArray();
        var manifest = new PackageManifest(
            2,
            runtimeIdentifier,
            version,
            "Release",
            true,
            files);
        File.WriteAllText(
            Path.Combine(stageRoot, ManifestFileName),
            JsonSerializer.Serialize(manifest, ManifestJsonOptions) + Environment.NewLine);
    }

    private static void CreateZip(
        string stageRoot,
        string archivePath,
        string executableName)
    {
        using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
        foreach (var file in Directory.GetFiles(stageRoot).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            entry.LastWriteTime = new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);
            entry.ExternalAttributes = string.Equals(name, executableName, StringComparison.Ordinal)
                ? Convert.ToInt32("100755", 8) << 16
                : Convert.ToInt32("100644", 8) << 16;
            using var source = File.OpenRead(file);
            using var destination = entry.Open();
            source.CopyTo(destination);
        }
    }

    private static void CreateTarGzip(
        string stageRoot,
        string archivePath,
        string executableName)
    {
        using var archiveStream = File.Create(archivePath);
        using var gzip = new GZipStream(archiveStream, CompressionLevel.Optimal);
        using var writer = new TarWriter(gzip, TarEntryFormat.Pax, leaveOpen: false);
        foreach (var file in Directory.GetFiles(stageRoot).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            using var source = File.OpenRead(file);
            var entry = new PaxTarEntry(TarEntryType.RegularFile, name)
            {
                DataStream = source,
                Gid = 0,
                Mode = string.Equals(name, executableName, StringComparison.Ordinal)
                    ? UnixFileMode.UserRead
                      | UnixFileMode.UserWrite
                      | UnixFileMode.UserExecute
                      | UnixFileMode.GroupRead
                      | UnixFileMode.GroupExecute
                      | UnixFileMode.OtherRead
                      | UnixFileMode.OtherExecute
                    : UnixFileMode.UserRead
                      | UnixFileMode.UserWrite
                      | UnixFileMode.GroupRead
                      | UnixFileMode.OtherRead,
                ModificationTime = DateTimeOffset.UnixEpoch,
                Uid = 0,
                UserName = string.Empty,
                GroupName = string.Empty,
            };
            writer.WriteEntry(entry);
        }
    }

    internal static void VerifyAndExtractArchive(
        string archivePath,
        string extractedRoot,
        string runtimeIdentifier,
        string version,
        string executableName,
        bool usesUnixArchive)
    {
        var expectedEntries = new HashSet<string>(StringComparer.Ordinal)
        {
            executableName,
            LicenceFileName,
            NoticesFileName,
            AngleLicenceFileName,
            ApacheLicenceFileName,
            DotNetLicenceFileName,
            DotNetNoticesFileName,
            InterLicenceFileName,
            SkiaNoticesFileName,
            ManifestFileName,
        };
        var actualEntries = usesUnixArchive
            ? ReadTarEntries(archivePath)
            : ReadZipEntries(archivePath);
        if (!expectedEntries.SetEquals(actualEntries)
            || actualEntries.Count != expectedEntries.Count)
        {
            throw new PackageException("archive-output-not-allowlisted");
        }

        if (usesUnixArchive)
        {
            using var archiveStream = File.OpenRead(archivePath);
            using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
            TarFile.ExtractToDirectory(gzip, extractedRoot, overwriteFiles: false);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    Path.Combine(extractedRoot, executableName),
                    UnixFileMode.UserRead
                    | UnixFileMode.UserWrite
                    | UnixFileMode.UserExecute
                    | UnixFileMode.GroupRead
                    | UnixFileMode.GroupExecute
                    | UnixFileMode.OtherRead
                    | UnixFileMode.OtherExecute);
            }
        }
        else
        {
            ZipFile.ExtractToDirectory(archivePath, extractedRoot);
        }

        var manifest = JsonSerializer.Deserialize<PackageManifest>(
            File.ReadAllText(Path.Combine(extractedRoot, ManifestFileName)),
            ManifestJsonOptions)
            ?? throw new PackageException("manifest-invalid");
        if (manifest.SchemaVersion != 2
            || !string.Equals(
                manifest.RuntimeIdentifier,
                runtimeIdentifier,
                StringComparison.Ordinal)
            || !string.Equals(manifest.Version, version, StringComparison.Ordinal)
            || !string.Equals(manifest.Configuration, "Release", StringComparison.Ordinal)
            || !manifest.SelfContained
            || manifest.Files.Length != expectedEntries.Count - 1)
        {
            throw new PackageException("manifest-invalid");
        }

        var expectedManifestPaths = expectedEntries
            .Where(path => !string.Equals(path, ManifestFileName, StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);
        var manifestPaths = manifest.Files
            .Select(file => file.Path)
            .ToHashSet(StringComparer.Ordinal);
        if (!expectedManifestPaths.SetEquals(manifestPaths)
            || manifestPaths.Count != manifest.Files.Length)
        {
            throw new PackageException("manifest-path-not-allowlisted");
        }

        foreach (var file in manifest.Files)
        {
            if (!expectedEntries.Contains(file.Path)
                || string.Equals(file.Path, ManifestFileName, StringComparison.Ordinal))
            {
                throw new PackageException("manifest-path-not-allowlisted");
            }

            var path = Path.Combine(extractedRoot, file.Path);
            if (!File.Exists(path)
                || new FileInfo(path).Length != file.Size
                || !string.Equals(
                    Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))),
                    file.Sha256,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new PackageException("manifest-integrity-failed");
            }
        }
    }

    private static HashSet<string> ReadZipEntries(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name) || !entries.Add(entry.FullName))
            {
                throw new PackageException("archive-output-not-allowlisted");
            }
        }

        return entries;
    }

    private static HashSet<string> ReadTarEntries(string archivePath)
    {
        using var archiveStream = File.OpenRead(archivePath);
        using var gzip = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzip);
        var entries = new HashSet<string>(StringComparer.Ordinal);
        while (reader.GetNextEntry() is { } entry)
        {
            if (entry.EntryType != TarEntryType.RegularFile || !entries.Add(entry.Name))
            {
                throw new PackageException("archive-output-not-allowlisted");
            }
        }

        return entries;
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    private sealed record RuntimePackage(
        string ExecutableName,
        string ArchiveExtension,
        bool UsesUnixArchive);

    private sealed record PackageManifest(
        int SchemaVersion,
        string RuntimeIdentifier,
        string Version,
        string Configuration,
        bool SelfContained,
        PackageFile[] Files);

    private sealed record PackageFile(string Path, long Size, string Sha256);
}
