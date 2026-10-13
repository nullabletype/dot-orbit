using DotOrbit.Desktop.ViewModels;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class AiViewModelTests
{
    [Fact]
    public void InterchangePreferenceDefaultsEnabledAndChangesOnlyAfterDevicePersistenceSucceeds()
    {
        var preferences = new RecordingInterchangePreferenceService();
        var ai = new AiViewModel(preferences);

        Assert.True(ai.IncludeAgentInstructionsAndSchemaWithSnapshot);
        Assert.Equal("Agent instructions and schemas included", ai.InterchangePreferenceStatus);

        ai.IncludeAgentInstructionsAndSchemaWithSnapshot = false;

        Assert.False(ai.IncludeAgentInstructionsAndSchemaWithSnapshot);
        Assert.Equal("Planning snapshots will be copied as JSON only.", ai.InterchangeStatus);

        preferences.AllowChange = false;
        ai.IncludeAgentInstructionsAndSchemaWithSnapshot = true;

        Assert.False(ai.IncludeAgentInstructionsAndSchemaWithSnapshot);
        Assert.Contains("previous setting", ai.InterchangeStatus, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReusableInstructionCopyReportsFixedSuccessAndFailureOutcomes()
    {
        var ai = new AiViewModel();
        var clipboard = new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard();

        await ai.CopyReusableInstructionsAsync(clipboard);

        Assert.Equal(1, clipboard.WriteCount);
        Assert.Equal("Reusable agent instructions and v1 schemas copied.", ai.InterchangeStatus);

        await ai.CopyReusableInstructionsAsync(
            new PlanningSnapshotCopyViewModelTests.RecordingPlainTextClipboard(
                new IOException("synthetic private content")));

        Assert.Equal(
            "Reusable agent instructions and schemas could not be copied. Try again.",
            ai.InterchangeStatus);
        Assert.DoesNotContain("private", ai.InterchangeStatus, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class RecordingInterchangePreferenceService : IInterchangePreferenceService
    {
        public bool IncludeAgentInstructionsAndSchemaWithSnapshot { get; private set; } = true;

        public bool AllowChange { get; set; } = true;

        public bool TrySetIncludeAgentInstructionsAndSchemaWithSnapshot(bool include)
        {
            if (!AllowChange)
            {
                return false;
            }

            IncludeAgentInstructionsAndSchemaWithSnapshot = include;
            return true;
        }
    }
}
