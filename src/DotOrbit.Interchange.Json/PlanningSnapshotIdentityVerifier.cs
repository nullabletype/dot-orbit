using System.Text.Json;
using DotOrbit.Core.Interchange;

namespace DotOrbit.Interchange.Json;

internal sealed class PlanningSnapshotIdentityVerifier : IPlanningSnapshotIdentityVerifier
{
    internal static PlanningSnapshotIdentityVerifier Instance { get; } = new();

    private PlanningSnapshotIdentityVerifier()
    {
    }

    public bool Matches(PlanningSnapshotContent snapshot, string expectedIdentity)
    {
        var generated = PlanningSnapshotJsonGenerator.Generate(snapshot);
        if (!generated.Succeeded)
        {
            return false;
        }

        using var document = JsonDocument.Parse(generated.Payload!.Value);
        return string.Equals(
            document.RootElement.GetProperty("snapshotId").GetString(),
            expectedIdentity,
            StringComparison.Ordinal);
    }
}
