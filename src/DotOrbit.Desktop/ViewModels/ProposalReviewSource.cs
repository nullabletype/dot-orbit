namespace DotOrbit.Desktop.ViewModels;

internal sealed record ProposalReviewSource(string Label, string Detail)
{
    public static ProposalReviewSource TextImport { get; } = new(
        "Text / JSON import",
        "Entered or pasted directly in dot-orbit.");

    public static ProposalReviewSource JsonFileImport { get; } = new(
        "JSON file import",
        "Loaded from a local file. The file path is not retained or shown.");

    public static ProposalReviewSource LocalMcp(string clientName) => new(
        $"{clientName} via local MCP",
        "Self-reported client name; access was approved for this operating-system account.");
}
