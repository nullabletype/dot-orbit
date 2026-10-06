using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DotOrbit.Packaging;
using Xunit;

namespace DotOrbit.Packaging.Tests;

public sealed class PackageArchiveTests
{
    [Fact]
    public void PackageOptionsRequireAndPreserveASemanticVersion()
    {
        var options = PackageOptions.Parse(
        [
            "--repository-root", ".",
            "--runtime", "osx-arm64",
            "--version", "1.2.3-rc.1+build.42",
            "--no-restore",
        ]);

        Assert.Equal("1.2.3-rc.1+build.42", options.Version);
        Assert.True(options.NoRestore);
    }

    [Theory]
    [InlineData("1.2")]
    [InlineData("01.2.3")]
    [InlineData("1.2.3-01")]
    [InlineData("v1.2.3")]
    public void PackageOptionsRejectNonCanonicalVersions(string version)
    {
        var exception = Assert.Throws<PackageException>(() => PackageOptions.Parse(
        [
            "--repository-root", ".",
            "--runtime", "osx-arm64",
            "--version", version,
        ]));

        Assert.Equal("invalid-version", exception.Message);
    }

    [Fact]
    public void PackageOptionsRequireAVersion()
    {
        var exception = Assert.Throws<PackageException>(() => PackageOptions.Parse(
        [
            "--repository-root", ".",
            "--runtime", "osx-arm64",
        ]));

        Assert.Equal("missing-required-arguments", exception.Message);
    }

    [Fact]
    public void PublishUsesTheExactPackageVersionWithoutAppendingTheCommit()
    {
        var arguments = PackageArchive.CreatePublishArguments(
            "DotOrbit.Desktop.csproj",
            "publish",
            "osx-arm64",
            "1.2.3-rc.1+build.42",
            noRestore: true);

        Assert.Contains("-p:Version=1.2.3-rc.1+build.42", arguments);
        Assert.Contains("-p:IncludeSourceRevisionInInformationalVersion=false", arguments);
        Assert.Contains("--no-restore", arguments);
    }

    [Fact]
    public void ValidatePublishedFilesAcceptsOnlyTheExpectedExecutable()
    {
        using var fixture = new PublishFixture("dot-orbit");

        var executable = PackageArchive.ValidatePublishedFiles(
            fixture.Directory,
            "dot-orbit");

        Assert.Equal(Path.Combine(fixture.Directory, "dot-orbit"), executable);
    }

    [Fact]
    public void ValidatePublishedFilesAllowsOptionalRootSymbolsWithoutPackagingThem()
    {
        using var fixture = new PublishFixture("dot-orbit.exe", "libSkiaSharp.pdb");

        var executable = PackageArchive.ValidatePublishedFiles(
            fixture.Directory,
            "dot-orbit.exe");

        Assert.Equal(Path.Combine(fixture.Directory, "dot-orbit.exe"), executable);
    }

    [Theory]
    [InlineData("source.cs")]
    [InlineData("test-results.trx")]
    [InlineData("screenshot.png")]
    [InlineData("obj/cache.bin")]
    public void ValidatePublishedFilesRejectsEveryAdditionalPayload(string relativePath)
    {
        using var fixture = new PublishFixture("dot-orbit", relativePath);

        var exception = Assert.Throws<PackageException>(() =>
            PackageArchive.ValidatePublishedFiles(fixture.Directory, "dot-orbit"));

        Assert.Equal("publish-output-not-allowlisted", exception.Message);
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "source.cs")]
    public void VerifyArchiveRejectsDuplicateOrUnexpectedEntries(
        bool duplicateEntry,
        string? unexpectedEntry)
    {
        using var fixture = new ArchiveFixture();
        fixture.CreateArchive(duplicateEntry, unexpectedEntry);

        var exception = Assert.Throws<PackageException>(() =>
            fixture.Verify());

        Assert.Equal("archive-output-not-allowlisted", exception.Message);
    }

    [Fact]
    public void VerifyArchiveRejectsAMissingManifestPayload()
    {
        using var fixture = new ArchiveFixture();
        fixture.CreateArchive(omitManifestPayload: true);

        var exception = Assert.Throws<PackageException>(() =>
            fixture.Verify());

        Assert.Equal("manifest-invalid", exception.Message);
    }

    [Fact]
    public void VerifyArchiveRejectsPayloadHashTampering()
    {
        using var fixture = new ArchiveFixture();
        fixture.CreateArchive(tamperPayload: true);

        var exception = Assert.Throws<PackageException>(() =>
            fixture.Verify());

        Assert.Equal("manifest-integrity-failed", exception.Message);
    }

    [Fact]
    public void VerifyArchiveRejectsAMismatchedPackageVersion()
    {
        using var fixture = new ArchiveFixture();
        fixture.CreateArchive(manifestVersion: "1.2.4");

        var exception = Assert.Throws<PackageException>(() => fixture.Verify());

        Assert.Equal("manifest-invalid", exception.Message);
    }

    private sealed class PublishFixture : IDisposable
    {
        public PublishFixture(params string[] files)
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                $"dot-orbit-package-tests-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            foreach (var file in files)
            {
                var path = Path.Combine(Directory, file);
                System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, file);
            }
        }

        public string Directory { get; }

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);
    }

    private sealed class ArchiveFixture : IDisposable
    {
        private static readonly string[] PayloadNames =
        [
            "dot-orbit.exe",
            "LICENSE.txt",
            "THIRD-PARTY-NOTICES.md",
            "ANGLE-LICENSE.txt",
            "APACHE-2.0.txt",
            "DOTNET-LICENSE.txt",
            "DOTNET-THIRD-PARTY-NOTICES.txt",
            "INTER-OFL-1.1.txt",
            "SKIA-HARFBUZZ-THIRD-PARTY-NOTICES.txt",
        ];

        public ArchiveFixture()
        {
            Directory = Path.Combine(
                Path.GetTempPath(),
                $"dot-orbit-archive-tests-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            ExtractedDirectory = Path.Combine(Directory, "extracted");
            System.IO.Directory.CreateDirectory(ExtractedDirectory);
            ArchivePath = Path.Combine(Directory, "package.zip");
        }

        public string Directory { get; }

        public string ExtractedDirectory { get; }

        public string ArchivePath { get; }

        public void CreateArchive(
            bool duplicateEntry = false,
            string? unexpectedEntry = null,
            bool omitManifestPayload = false,
            bool tamperPayload = false,
            string manifestVersion = "1.2.3")
        {
            var payloads = PayloadNames.ToDictionary(
                name => name,
                name => Encoding.UTF8.GetBytes($"payload:{name}"),
                StringComparer.Ordinal);
            var manifestFiles = payloads
                .Where(pair =>
                    !omitManifestPayload
                    || !string.Equals(pair.Key, "LICENSE.txt", StringComparison.Ordinal))
                .Select(pair => new
                {
                    path = pair.Key,
                    size = pair.Value.LongLength,
                    sha256 = Convert.ToHexString(SHA256.HashData(pair.Value)).ToLowerInvariant(),
                })
                .ToArray();
            var manifest = JsonSerializer.Serialize(new
            {
                schemaVersion = 2,
                runtimeIdentifier = "win-x64",
                version = manifestVersion,
                configuration = "Release",
                selfContained = true,
                files = manifestFiles,
            });

            using var archive = ZipFile.Open(ArchivePath, ZipArchiveMode.Create);
            foreach (var payload in payloads)
            {
                var content = tamperPayload
                    && string.Equals(payload.Key, "LICENSE.txt", StringComparison.Ordinal)
                        ? "tampered"u8.ToArray()
                        : payload.Value;
                WriteEntry(archive, payload.Key, content);
            }

            WriteEntry(archive, "package-manifest.json", Encoding.UTF8.GetBytes(manifest));
            if (duplicateEntry)
            {
                WriteEntry(archive, "LICENSE.txt", "duplicate"u8.ToArray());
            }

            if (unexpectedEntry is not null)
            {
                WriteEntry(archive, unexpectedEntry, "unexpected"u8.ToArray());
            }
        }

        public void Verify() => PackageArchive.VerifyAndExtractArchive(
            ArchivePath,
            ExtractedDirectory,
            "win-x64",
            "1.2.3",
            "dot-orbit.exe",
            usesUnixArchive: false);

        public void Dispose() => System.IO.Directory.Delete(Directory, recursive: true);

        private static void WriteEntry(
            ZipArchive archive,
            string name,
            byte[] content)
        {
            var entry = archive.CreateEntry(name);
            using var stream = entry.Open();
            stream.Write(content);
        }
    }
}
