using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace DotOrbit.Desktop.ApplicationUpdates;

internal sealed record ApplicationRelease(SemanticVersion Version)
{
    public Uri ReleasePage => new(
        $"https://github.com/nullabletype/dot-orbit/releases/tag/v{Uri.EscapeDataString(Version.Text)}");
}

internal enum ReleaseCatalogResultKind
{
    Success,
    NotModified,
    Unavailable,
}

internal sealed record ReleaseCatalogResult(
    ReleaseCatalogResultKind Kind,
    IReadOnlyList<ApplicationRelease> Releases,
    string? ETag)
{
    public static ReleaseCatalogResult Unavailable { get; } =
        new(ReleaseCatalogResultKind.Unavailable, [], null);
}

internal interface IReleaseCatalogClient
{
    Task<ReleaseCatalogResult> GetAsync(string? etag, CancellationToken cancellationToken);
}

internal static class ApplicationUpdateHttpClient
{
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        UseCookies = false,
    };
}

internal sealed class GitHubReleaseCatalogClient : IReleaseCatalogClient
{
    internal const string ApiVersion = "2026-03-10";
    internal const string ReleasesUri =
        "https://api.github.com/repositories/1386405384/releases?per_page=100";
    private const int MaximumPages = 4;
    private const int MaximumReleases = 400;
    private const int MaximumResponseBytes = 1024 * 1024;
    private const int MaximumJsonDepth = 32;
    private static readonly TimeSpan DefaultRequestDeadline = TimeSpan.FromSeconds(10);
    private static readonly string[] RequiredAssets =
    [
        "dot-orbit-linux-x64.tar.gz",
        "dot-orbit-osx-arm64.tar.gz",
        "dot-orbit-osx-x64.tar.gz",
        "dot-orbit-win-x64.zip",
    ];
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan _requestDeadline;

    public GitHubReleaseCatalogClient(
        HttpClient httpClient,
        TimeProvider? timeProvider = null,
        TimeSpan? requestDeadline = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _requestDeadline = requestDeadline ?? DefaultRequestDeadline;
        if (_requestDeadline <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestDeadline),
                "The update request deadline must be positive.");
        }
    }

    public async Task<ReleaseCatalogResult> GetAsync(
        string? etag,
        CancellationToken cancellationToken)
    {
        using var deadline = new CancellationTokenSource(_requestDeadline, _timeProvider);
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            deadline.Token);
        var requestCancellationToken = linkedCancellation.Token;
        Uri? next = new(ReleasesUri);
        var releases = new List<ApplicationRelease>();
        var releaseCount = 0;
        string? responseEtag = null;
        try
        {
            for (var page = 0; page < MaximumPages && next is not null; page++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, next);
                request.Headers.Accept.Add(new("application/vnd.github+json"));
                request.Headers.UserAgent.Add(new ProductInfoHeaderValue("dot-orbit", "update-check"));
                request.Headers.Add("X-GitHub-Api-Version", ApiVersion);
                if (page == 0 && !string.IsNullOrWhiteSpace(etag))
                {
                    request.Headers.IfNoneMatch.Add(EntityTagHeaderValue.Parse(etag));
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    requestCancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotModified)
                {
                    return page == 0 && request.Headers.IfNoneMatch.Count > 0
                        ? new(ReleaseCatalogResultKind.NotModified, [], etag)
                        : ReleaseCatalogResult.Unavailable;
                }

                if (response.StatusCode != HttpStatusCode.OK
                    || response.Content.Headers.ContentLength > MaximumResponseBytes)
                {
                    return ReleaseCatalogResult.Unavailable;
                }

                var bytes = await ReadBoundedAsync(
                    response.Content,
                    requestCancellationToken).ConfigureAwait(false);
                if (bytes is null)
                {
                    return ReleaseCatalogResult.Unavailable;
                }

                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    MaxDepth = MaximumJsonDepth,
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                });
                if (document.RootElement.ValueKind is not JsonValueKind.Array)
                {
                    return ReleaseCatalogResult.Unavailable;
                }

                foreach (var release in document.RootElement.EnumerateArray())
                {
                    if (TryAdmit(release, out var admitted))
                    {
                        releases.Add(admitted);
                    }

                    if (++releaseCount > MaximumReleases)
                    {
                        return ReleaseCatalogResult.Unavailable;
                    }
                }

                if (page == 0 && response.Headers.ETag is { } firstPageEtag)
                {
                    var candidateEtag = firstPageEtag.ToString();
                    if (candidateEtag.Length > 256)
                    {
                        return ReleaseCatalogResult.Unavailable;
                    }

                    responseEtag = candidateEtag;
                }
                next = TryGetNextPage(response.Headers, out var nextPage) ? nextPage : null;
                if (next is not null && page == MaximumPages - 1)
                {
                    return ReleaseCatalogResult.Unavailable;
                }
            }

            return HasAmbiguousPrecedence(releases)
                ? ReleaseCatalogResult.Unavailable
                : new(ReleaseCatalogResultKind.Success, releases, responseEtag);
        }
        catch (Exception exception) when (exception is HttpRequestException
                                          or IOException
                                          or JsonException
                                          or FormatException
                                          or InvalidOperationException)
        {
            return ReleaseCatalogResult.Unavailable;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return ReleaseCatalogResult.Unavailable;
        }
    }

    private static async Task<byte[]?> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false);
            if (read == 0) return buffer.ToArray();
            if (buffer.Length + read > MaximumResponseBytes) return null;
            buffer.Write(chunk, 0, read);
        }
    }

    private static bool TryAdmit(JsonElement release, out ApplicationRelease admitted)
    {
        admitted = null!;
        if (release.ValueKind is not JsonValueKind.Object
            || !TryGetBoolean(release, "draft", out var draft) || draft
            || !TryGetBoolean(release, "immutable", out var immutable) || !immutable
            || !TryGetBoolean(release, "prerelease", out var prerelease)
            || !release.TryGetProperty("published_at", out var publishedAt)
            || publishedAt.ValueKind is not JsonValueKind.String
            || !publishedAt.TryGetDateTimeOffset(out _)
            || !release.TryGetProperty("tag_name", out var tagElement)
            || tagElement.ValueKind is not JsonValueKind.String)
        {
            return false;
        }

        var tag = tagElement.GetString();
        if (tag is null || !tag.StartsWith('v')
            || !SemanticVersion.TryParse(tag[1..], out var version)
            || !string.Equals(tag, $"v{version.Text}", StringComparison.Ordinal)
            || prerelease != version.IsPrerelease
            || !HasRequiredAssets(release))
        {
            return false;
        }

        admitted = new(version);
        return true;
    }

    private static bool HasRequiredAssets(JsonElement release)
    {
        if (!release.TryGetProperty("assets", out var assets)
            || assets.ValueKind is not JsonValueKind.Array)
        {
            return false;
        }

        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var asset in assets.EnumerateArray())
        {
            if (asset.ValueKind is not JsonValueKind.Object
                || !TryGetString(asset, "name", out var name)
                || !RequiredAssets.Contains(name, StringComparer.Ordinal)
                || !found.Add(name)
                || !TryGetString(asset, "state", out var state)
                || !string.Equals(state, "uploaded", StringComparison.Ordinal)
                || !asset.TryGetProperty("size", out var size)
                || !size.TryGetInt64(out var byteCount)
                || byteCount <= 0
                || !TryGetString(asset, "digest", out var digest)
                || !IsSha256Digest(digest))
            {
                return false;
            }
        }

        return found.Count == RequiredAssets.Length;
    }

    private static bool IsSha256Digest(string digest) =>
        digest.Length == 71
        && digest.StartsWith("sha256:", StringComparison.Ordinal)
        && digest.AsSpan(7).ToArray().All(character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f');

    private static bool TryGetBoolean(JsonElement element, string name, out bool value)
    {
        value = false;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(name, out var property)
            || property.ValueKind is not JsonValueKind.String
            || property.GetString() is not { } text)
        {
            return false;
        }

        value = text;
        return true;
    }

    private static bool TryGetNextPage(HttpResponseHeaders headers, out Uri? next)
    {
        next = null;
        if (!headers.TryGetValues("Link", out var values)) return false;
        foreach (var value in values.SelectMany(value => value.Split(',')))
        {
            var sections = value.Split(';', StringSplitOptions.TrimEntries);
            if (sections.Length < 2)
            {
                throw new InvalidOperationException("invalid-pagination-link");
            }

            if (!TryReadNextRelation(sections.Skip(1), out var hasNextRelation))
            {
                throw new InvalidOperationException("missing-pagination-relation");
            }

            if (!hasNextRelation)
            {
                continue;
            }

            var target = sections[0];
            if (target.Length < 3 || target[0] != '<' || target[^1] != '>'
                || !Uri.TryCreate(target[1..^1], UriKind.Absolute, out var candidate)
                || !string.Equals(candidate.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)
                || !string.Equals(candidate.Host, "api.github.com", StringComparison.OrdinalIgnoreCase)
                || !candidate.IsDefaultPort
                || !string.IsNullOrEmpty(candidate.UserInfo)
                || !string.Equals(
                    candidate.AbsolutePath,
                    "/repositories/1386405384/releases",
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException("invalid-pagination-target");
            }

            next = candidate;
            return true;
        }

        return false;
    }

    private static bool TryReadNextRelation(
        IEnumerable<string> parameters,
        out bool hasNextRelation)
    {
        hasNextRelation = false;
        foreach (var parameter in parameters)
        {
            var separator = parameter.IndexOf('=');
            if (separator < 0
                || !string.Equals(
                    parameter[..separator].Trim(),
                    "rel",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = parameter[(separator + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '"' && value[^1] == '"')
            {
                value = value[1..^1];
            }
            else if (value.StartsWith('"') || value.EndsWith('"'))
            {
                throw new InvalidOperationException("invalid-pagination-relation");
            }

            var relations = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (relations.Length == 0)
            {
                throw new InvalidOperationException("invalid-pagination-relation");
            }

            hasNextRelation = relations.Contains("next", StringComparer.OrdinalIgnoreCase);
            return true;
        }

        return false;
    }

    private static bool HasAmbiguousPrecedence(IReadOnlyList<ApplicationRelease> releases) =>
        releases.SelectMany((left, index) => releases.Skip(index + 1).Select(right => (left, right)))
            .Any(pair => pair.left.Version.CompareTo(pair.right.Version) == 0
                         && !pair.left.Version.Equals(pair.right.Version));
}
