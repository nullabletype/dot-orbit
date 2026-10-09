using System.Text.Json;
using DotOrbit.Core.Diagnostics;
using DotOrbit.Core.Workspaces;
using DotOrbit.Storage.Sqlite;
using Xunit;

namespace DotOrbit.Storage.Sqlite.Tests;

[CollectionDefinition("Performance trace", DisableParallelization = true)]
public sealed class PerformanceTraceTestGroup;

[Collection("Performance trace")]
public sealed class PerformanceTraceIntegrationTests
{
    [Fact]
    public void WarmEncryptedOperationsSeparateStagesWithoutReopeningOrExposingContent()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"dot-orbit-trace-test-{Guid.NewGuid():N}");
        try
        {
            var passphrase = WorkspacePassphrase.Create("private synthetic password", "private synthetic password").Passphrase!;
            var result = new EncryptedWorkspaceStore().Create(
                Path.Combine(directory, "private-path.orb"), passphrase, CategoryName.Create("private category").CategoryName!);
            using var session = Assert.IsAssignableFrom<IWorkspaceSession>(result.Session);
            var category = Assert.Single(session.Work.Read().Categories);
            var task = session.Work.CreateStandaloneTask("private task", "private description", category.Id, null);
            using var recording = PerformanceTrace.Start();

            session.Work.SetTaskTodayLane(task.Id, TodayLane.Planned);
            session.Work.Read();
            Assert.Throws<ArgumentException>(() => session.Work.SetTaskTodayLane("private missing id", TodayLane.Planned));
            recording.Dispose();

            var samples = recording.Snapshot();
            var write = Assert.Single(samples, sample => sample.Stage == PerformanceStage.StorageWrite
                && sample.Operation == PerformanceOperation.SetTaskTodayLane
                && samples.Any(child => child.ParentId == sample.Id && child.Stage == PerformanceStage.Commit));
            Assert.Contains(samples, sample => sample.ParentId == write.Id && sample.Stage == PerformanceStage.GateWait);
            Assert.DoesNotContain(samples, sample => sample.Stage == PerformanceStage.ConnectionOpen);
            Assert.DoesNotContain(samples, sample => sample.Stage == PerformanceStage.ConnectionConfigure);
            Assert.Contains(samples, sample => sample.ParentId == write.Id && sample.Stage == PerformanceStage.Mutation);
            Assert.Contains(samples, sample => sample.ParentId == write.Id && sample.Stage == PerformanceStage.DerivedStorage);
            Assert.Contains(samples, sample => sample.ParentId == write.Id && sample.Stage == PerformanceStage.Recovery);
            Assert.Contains(samples, sample => sample.Stage == PerformanceStage.StorageRead && sample.Operation == PerformanceOperation.Read);
            Assert.Equal(2, samples.Count(sample => sample.Stage == PerformanceStage.StorageWrite));
            Assert.DoesNotContain("private", JsonSerializer.Serialize(samples), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }
}
