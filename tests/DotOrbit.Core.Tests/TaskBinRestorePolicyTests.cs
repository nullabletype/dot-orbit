using DotOrbit.Core.Workspaces;
using Xunit;

namespace DotOrbit.Core.Tests;

public sealed class TaskBinRestorePolicyTests
{
    public static TheoryData<string[], TaskBinOrderAnchor[], int> RestoreCases => new()
    {
        { ["before", "after"], [new("before", -1), new("after", 1)], 1 },
        { ["far-before", "other"], [new("missing", -1), new("far-before", -2), new("missing-after", 1)], 1 },
        { ["other", "far-after"], [new("missing", -1), new("missing-after", 1), new("far-after", 2)], 1 },
        { ["near-after", "far-before"], [new("far-before", -2), new("near-after", 1)], 0 },
        { ["far-after", "near-before"], [new("near-before", -1), new("far-after", 2)], 2 },
        { ["other"], [new("missing", -1), new("also-missing", 1)], 1 },
        { [], [], 0 },
    };

    [Theory]
    [MemberData(nameof(RestoreCases))]
    public void RestorePositionUsesExactGapNearestSurvivingNeighbourOrEnd(
        string[] survivingIds,
        TaskBinOrderAnchor[] formerOrder,
        int expectedIndex)
    {
        Assert.Equal(expectedIndex,
            TaskBinRestorePolicy.RestoreIndex(survivingIds, formerOrder));
    }

    [Fact]
    public void TaskCannotRestoreWhileParentProjectIsBinned()
    {
        var exception = Assert.Throws<InvalidOperationException>(
            () => TaskBinRestorePolicy.EnsureParentAllowsRestore(parentProjectIsBinned: true));

        Assert.Contains("parent Project", exception.Message, StringComparison.Ordinal);
        TaskBinRestorePolicy.EnsureParentAllowsRestore(parentProjectIsBinned: false);
    }
}
