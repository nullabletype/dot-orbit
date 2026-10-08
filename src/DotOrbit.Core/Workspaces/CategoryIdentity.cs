namespace DotOrbit.Core.Workspaces;

public sealed record CategoryIdentity(string ColourKey)
{
    public static CategoryIdentity Create(string colourKey)
    {
        if (!IdentityColourPalette.IsSupported(colourKey))
            throw new ArgumentException("Choose a supported Category colour.", nameof(colourKey));
        return new(colourKey);
    }
}

public sealed record ProjectIdentity(string ColourKey)
{
    public static ProjectIdentity Create(string colourKey)
    {
        if (!IdentityColourPalette.IsSupported(colourKey))
            throw new ArgumentException("Choose a supported Project colour.", nameof(colourKey));
        return new(colourKey);
    }
}

public static class IdentityColourPalette
{
    public const string DefaultKey = "orchid";

    public static IReadOnlyList<string> Keys { get; } =
        Array.AsReadOnly([
            DefaultKey, "violet", "indigo", "ocean", "teal", "lime", "tangerine", "rose",
            "cobalt", "cyan", "emerald", "gold", "amber", "coral", "magenta", "slate",
        ]);

    public static bool IsSupported(string? key) =>
        key is not null && Keys.Contains(key, StringComparer.Ordinal);

    public static string KeyForPosition(long position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        return Keys[checked((int)(position % Keys.Count))];
    }

    public static string KeyForProjectPosition(long position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        const int projectOffset = 5;
        return Keys[checked((int)((position + projectOffset) % Keys.Count))];
    }
}
