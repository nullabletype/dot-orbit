using System.Text.Json;
using DotOrbit.Core.Interchange;

namespace DotOrbit.Interchange.Json;

internal sealed class PlanningSnapshotIdentityVerifier(int schemaVersion) : IPlanningSnapshotIdentityVerifier
{
    public bool Matches(PlanningSnapshotContent snapshot, string expectedIdentity)
    {
        var generated = PlanningSnapshotJsonGenerator.Generate(snapshot, schemaVersion);
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
