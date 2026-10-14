using System.Text;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class ProposalFileIntakeTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "orbit-proposal-intake-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RegularStrictUtf8FileLoadsWithoutReturningItsPath()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "proposal.json");
        await File.WriteAllTextAsync(path, "{\"title\":\"Synthetic café\"}", new UTF8Encoding(false, true), TestContext.Current.CancellationToken);

        var result = await new ProposalFileIntake().ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(ProposalFileIntakeStatus.Loaded, result.Status);
        Assert.Equal("{\"title\":\"Synthetic café\"}", result.Text);
        Assert.DoesNotContain(path, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Synthetic café", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExactUtf8LimitLoadsAndRelativePathIsRejected()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "proposal.json");
        await File.WriteAllBytesAsync(
            path,
            new byte[DotOrbit.Interchange.Json.ChangeProposalJsonParser.MaximumInputBytes],
            TestContext.Current.CancellationToken);
        var intake = new ProposalFileIntake();

        var exact = await intake.ReadAsync(path, TestContext.Current.CancellationToken);
        var relative = await intake.ReadAsync("proposal.json", TestContext.Current.CancellationToken);

        Assert.Equal(ProposalFileIntakeStatus.Loaded, exact.Status);
        Assert.Equal(DotOrbit.Interchange.Json.ChangeProposalJsonParser.MaximumInputBytes, exact.Text!.Length);
        Assert.Equal(ProposalFileIntakeStatus.Rejected, relative.Status);
        Assert.Null(relative.Text);
    }

    [Fact]
    public async Task DirectorySymlinkDeviceBomInvalidUtf8AndLimitPlusOneAreRejected()
    {
        Directory.CreateDirectory(_directory);
        var regular = Path.Combine(_directory, "regular.json");
        await File.WriteAllTextAsync(regular, "{}", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        var link = Path.Combine(_directory, "link.json");
        File.CreateSymbolicLink(link, regular);
        var bom = Path.Combine(_directory, "bom.json");
        await File.WriteAllBytesAsync(bom, [0xef, 0xbb, 0xbf, 0x7b, 0x7d], TestContext.Current.CancellationToken);
        var invalid = Path.Combine(_directory, "invalid.json");
        await File.WriteAllBytesAsync(invalid, [0xff], TestContext.Current.CancellationToken);
        var tooLarge = Path.Combine(_directory, "large.json");
        await File.WriteAllBytesAsync(tooLarge, new byte[DotOrbit.Interchange.Json.ChangeProposalJsonParser.MaximumInputBytes + 1], TestContext.Current.CancellationToken);
        var intake = new ProposalFileIntake();

        Assert.Equal(ProposalFileIntakeStatus.Rejected, (await intake.ReadAsync(_directory, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ProposalFileIntakeStatus.Rejected, (await intake.ReadAsync(link, TestContext.Current.CancellationToken)).Status);
        if (!OperatingSystem.IsWindows())
            Assert.Equal(ProposalFileIntakeStatus.Rejected, (await intake.ReadAsync("/dev/null", TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ProposalFileIntakeStatus.Rejected, (await intake.ReadAsync(bom, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ProposalFileIntakeStatus.Rejected, (await intake.ReadAsync(invalid, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(ProposalFileIntakeStatus.Rejected, (await intake.ReadAsync(tooLarge, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task CancelledReadReturnsFixedCancellationWithoutContent()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "proposal.json");
        await File.WriteAllTextAsync(path, "{}", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cancellation.Cancel();

        var result = await new ProposalFileIntake().ReadAsync(path, cancellation.Token);

        Assert.Equal(ProposalFileIntakeStatus.Cancelled, result.Status);
        Assert.Null(result.Text);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}
