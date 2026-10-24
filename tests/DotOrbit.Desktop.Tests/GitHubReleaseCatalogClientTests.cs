using System.Net;
using System.Text;
using DotOrbit.Desktop.ApplicationUpdates;
using Xunit;

namespace DotOrbit.Desktop.Tests;

public sealed class GitHubReleaseCatalogClientTests
{
    [Fact]
    public async Task RequestUsesFixedAnonymousApiContractAndAdmitsOnlyCompleteImmutableRelease()
    {
        HttpRequestMessage? observed = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            observed = Clone(request);
            var response = JsonResponse($"[{ReleaseJson("v2.1.0", prerelease: false)}]");
            response.Headers.ETag = new("\"catalog-1\"", isWeak: true);
            return response;
        });
        var client = new GitHubReleaseCatalogClient(new HttpClient(handler));

        var result = await client.GetAsync("W/\"prior\"", TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Success, result.Kind);
        Assert.Equal("2.1.0", Assert.Single(result.Releases).Version.Text);
        Assert.Equal("W/\"catalog-1\"", result.ETag);
        Assert.Equal(GitHubReleaseCatalogClient.ReleasesUri, observed!.RequestUri!.AbsoluteUri);
        Assert.Equal(HttpMethod.Get, observed.Method);
        Assert.Equal(GitHubReleaseCatalogClient.ApiVersion, observed.Headers.GetValues("X-GitHub-Api-Version").Single());
        Assert.Equal("W/\"prior\"", observed.Headers.IfNoneMatch.Single().ToString());
        Assert.Null(observed.Headers.Authorization);
        Assert.Null(observed.Content);
        Assert.Equal("dot-orbit/update-check", observed.Headers.UserAgent.ToString());
        Assert.DoesNotContain("version", observed.RequestUri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("mutable")]
    [InlineData("unpublished")]
    [InlineData("invalid-published-at")]
    [InlineData("noncanonical-tag")]
    [InlineData("channel")]
    [InlineData("missing-asset")]
    [InlineData("extra-asset")]
    [InlineData("duplicate-asset")]
    [InlineData("non-uploaded-asset")]
    [InlineData("digestless-asset")]
    [InlineData("empty-asset")]
    [InlineData("bad-digest")]
    public async Task IncompleteOrUntrustedReleaseIsIgnored(string mutation)
    {
        var json = ReleaseJson("v2.1.0-beta.1", prerelease: true);
        json = mutation switch
        {
            "draft" => json.Replace("\"draft\":false", "\"draft\":true", StringComparison.Ordinal),
            "mutable" => json.Replace("\"immutable\":true", "\"immutable\":false", StringComparison.Ordinal),
            "unpublished" => json.Replace("\"published_at\":\"2026-10-24T12:00:00Z\"", "\"published_at\":null", StringComparison.Ordinal),
            "invalid-published-at" => json.Replace("2026-10-24T12:00:00Z", "not-a-date", StringComparison.Ordinal),
            "noncanonical-tag" => json.Replace("v2.1.0-beta.1", "v02.1.0-beta.1", StringComparison.Ordinal),
            "channel" => json.Replace("\"prerelease\":true", "\"prerelease\":false", StringComparison.Ordinal),
            "missing-asset" => json.Replace(AssetJson("dot-orbit-win-x64.zip") + ",", string.Empty, StringComparison.Ordinal),
            "extra-asset" => json.Replace("]}", $",{AssetJson("extra.zip")}]}}", StringComparison.Ordinal),
            "duplicate-asset" => json.Replace("]}", $",{AssetJson("dot-orbit-win-x64.zip")}]}}", StringComparison.Ordinal),
            "non-uploaded-asset" => json.Replace("\"state\":\"uploaded\"", "\"state\":\"new\"", StringComparison.Ordinal),
            "digestless-asset" => json.Replace($",\"digest\":\"sha256:{new string('a', 64)}\"", string.Empty, StringComparison.Ordinal),
            "empty-asset" => json.Replace("\"size\":42", "\"size\":0", StringComparison.Ordinal),
            "bad-digest" => json.Replace(new string('a', 64), new string('A', 64), StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        var client = CreateClient(_ => JsonResponse($"[{json}]"));

        var result = await client.GetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Success, result.Kind);
        Assert.Empty(result.Releases);
    }

    [Fact]
    public async Task NotModifiedAndTransportFailuresHaveExplicitOutcomes()
    {
        var notModified = CreateClient(_ => new(HttpStatusCode.NotModified));
        var failed = CreateClient(_ => new(HttpStatusCode.TooManyRequests));
        var redirect = CreateClient(_ => new(HttpStatusCode.Redirect));

        var cached = await notModified.GetAsync("\"known\"", TestContext.Current.CancellationToken);
        var unavailable = await failed.GetAsync(null, TestContext.Current.CancellationToken);
        var redirected = await redirect.GetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.NotModified, cached.Kind);
        Assert.Equal(ReleaseCatalogResultKind.Unavailable, unavailable.Kind);
        Assert.Equal(ReleaseCatalogResultKind.Unavailable, redirected.Kind);

        var unsolicited = await notModified.GetAsync(null, TestContext.Current.CancellationToken);
        Assert.Equal(ReleaseCatalogResultKind.Unavailable, unsolicited.Kind);
    }

    [Fact]
    public async Task SameOriginPaginationCombinesVerifiedPages()
    {
        var calls = 0;
        var client = CreateClient(_ =>
        {
            calls++;
            var response = JsonResponse(calls == 1
                ? $"[{ReleaseJson("v1.0.0", prerelease: false)}]"
                : $"[{ReleaseJson("v1.1.0", prerelease: false)}]");
            if (calls == 1)
            {
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    "<https://api.github.com/repositories/1386405384/releases?per_page=100&page=2>; rel=\"next\"");
            }

            return response;
        });

        var result = await client.GetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Success, result.Kind);
        Assert.Equal(["1.0.0", "1.1.0"], result.Releases.Select(item => item.Version.Text));
        Assert.Equal(2, calls);
    }

    [Theory]
    [InlineData("rel=next")]
    [InlineData("rel=\"prev next\"")]
    [InlineData("REL=\"NEXT\"")]
    public async Task PaginationRecognizesSupportedNextRelationForms(string relation)
    {
        var calls = 0;
        var client = CreateClient(_ =>
        {
            calls++;
            var response = JsonResponse("[]");
            if (calls == 1)
            {
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    $"<https://api.github.com/repositories/1386405384/releases?page=2>; {relation}");
            }

            return response;
        });

        var result = await client.GetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Success, result.Kind);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task OnlyASafeFirstPageEtagCanBeCached()
    {
        var calls = 0;
        var secondPageOnly = CreateClient(_ =>
        {
            calls++;
            var response = JsonResponse("[]");
            if (calls == 1)
            {
                response.Headers.TryAddWithoutValidation(
                    "Link",
                    "<https://api.github.com/repositories/1386405384/releases?page=2>; rel=\"next\"");
            }
            else
            {
                response.Headers.ETag = new("\"second-page\"");
            }

            return response;
        });
        var oversized = CreateClient(_ =>
        {
            var response = JsonResponse("[]");
            response.Headers.ETag = new($"\"{new string('a', 257)}\"");
            return response;
        });

        var noFirstPageValidator = await secondPageOnly.GetAsync(
            null,
            TestContext.Current.CancellationToken);
        var oversizedResult = await oversized.GetAsync(
            null,
            TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Success, noFirstPageValidator.Kind);
        Assert.Null(noFirstPageValidator.ETag);
        Assert.Equal(ReleaseCatalogResultKind.Unavailable, oversizedResult.Kind);
    }

    [Fact]
    public async Task CrossOriginPaginationFailsClosed()
    {
        var calls = 0;
        var client = CreateClient(_ =>
        {
            calls++;
            var response = JsonResponse("[]");
            response.Headers.TryAddWithoutValidation(
                "Link",
                "<https://example.com/repositories/1386405384/releases?page=2>; rel=\"next\"");
            return response;
        });

        var result = await client.GetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Unavailable, result.Kind);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task PaginationBeyondTheBoundAndOversizedResponsesFailClosed()
    {
        var page = 0;
        var paged = CreateClient(_ =>
        {
            page++;
            var response = JsonResponse("[]");
            response.Headers.TryAddWithoutValidation(
                "Link",
                $"<https://api.github.com/repositories/1386405384/releases?per_page=100&page={page + 1}>; rel=\"next\"");
            return response;
        });
        var oversized = CreateClient(_ => new(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[(1024 * 1024) + 1]),
        });

        var pagedResult = await paged.GetAsync(null, TestContext.Current.CancellationToken);
        var oversizedResult = await oversized.GetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Unavailable, pagedResult.Kind);
        Assert.Equal(4, page);
        Assert.Equal(ReleaseCatalogResultKind.Unavailable, oversizedResult.Kind);
    }

    [Fact]
    public async Task MalformedJsonAndExcessiveCatalogSizeFailClosed()
    {
        var malformed = CreateClient(_ => JsonResponse("[{"));
        var excessive = CreateClient(_ => JsonResponse(
            $"[{string.Join(',', Enumerable.Repeat("{}", 401))}]"));

        var malformedResult = await malformed.GetAsync(null, TestContext.Current.CancellationToken);
        var excessiveResult = await excessive.GetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Unavailable, malformedResult.Kind);
        Assert.Equal(ReleaseCatalogResultKind.Unavailable, excessiveResult.Kind);
    }

    [Fact]
    public async Task ReleasesWithEqualPrecedenceAndDifferentBuildMetadataFailClosed()
    {
        var client = CreateClient(_ => JsonResponse(
            $"[{ReleaseJson("v1.2.3+first", prerelease: false)},{ReleaseJson("v1.2.3+second", prerelease: false)}]"));

        var result = await client.GetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Unavailable, result.Kind);
    }

    [Fact]
    public async Task DeadlineIncludesReadingAStalledResponseBody()
    {
        var readStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var time = new ManualDeadlineTimeProvider(DateTimeOffset.UnixEpoch);
        var client = new GitHubReleaseCatalogClient(
            new HttpClient(new StubHttpMessageHandler(_ => new(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingReadStream(readStarted)),
            })),
            time,
            TimeSpan.FromSeconds(10));

        var pending = client.GetAsync(null, TestContext.Current.CancellationToken);
        await readStarted.Task.WaitAsync(TestContext.Current.CancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));

        var result = await pending;

        Assert.Equal(ReleaseCatalogResultKind.Unavailable, result.Kind);
    }

    [Fact]
    public void ProductionHandlerDisablesRedirectsAndCookies()
    {
        using var handler = ApplicationUpdateHttpClient.CreateHandler();

        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
    }

    [Fact]
    public async Task InternalTimeoutIsUnavailableButCallerCancellationPropagates()
    {
        var timeout = new GitHubReleaseCatalogClient(new HttpClient(
            new CancellingHttpMessageHandler(callerCancellation: false)));
        var cancellation = new GitHubReleaseCatalogClient(new HttpClient(
            new CancellingHttpMessageHandler(callerCancellation: true)));
        using var source = new CancellationTokenSource();
        source.Cancel();

        var timeoutResult = await timeout.GetAsync(null, TestContext.Current.CancellationToken);

        Assert.Equal(ReleaseCatalogResultKind.Unavailable, timeoutResult.Kind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            cancellation.GetAsync(null, source.Token));
    }

    private static GitHubReleaseCatalogClient CreateClient(
        Func<HttpRequestMessage, HttpResponseMessage> response) =>
        new(new HttpClient(new StubHttpMessageHandler(response)));

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json"),
    };

    private static string ReleaseJson(string tag, bool prerelease) =>
        $$"""
        {"draft":false,"immutable":true,"prerelease":{{prerelease.ToString().ToLowerInvariant()}},"published_at":"2026-10-24T12:00:00Z","tag_name":"{{tag}}","assets":[{{AssetJson("dot-orbit-win-x64.zip")}},{{AssetJson("dot-orbit-linux-x64.tar.gz")}},{{AssetJson("dot-orbit-osx-x64.tar.gz")}},{{AssetJson("dot-orbit-osx-arm64.tar.gz")}}]}
        """;

    private static string AssetJson(string name) =>
        $$"""{"name":"{{name}}","state":"uploaded","size":42,"digest":"sha256:{{new string('a', 64)}}"}""";

    private static HttpRequestMessage Clone(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        foreach (var header in request.Headers)
        {
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }

    private sealed class StubHttpMessageHandler(
        Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response(request));
    }

    private sealed class CancellingHttpMessageHandler(bool callerCancellation) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => callerCancellation
                ? Task.FromCanceled<HttpResponseMessage>(cancellationToken)
                : Task.FromException<HttpResponseMessage>(new TaskCanceledException("timeout"));
    }

    private sealed class BlockingReadStream(TaskCompletionSource readStarted) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            readStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class ManualDeadlineTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private readonly List<ManualTimer> _timers = [];

        public override DateTimeOffset GetUtcNow() => now;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, now + dueTime);
            _timers.Add(timer);
            return timer;
        }

        public void Advance(TimeSpan elapsed)
        {
            now += elapsed;
            foreach (var timer in _timers.Where(timer => timer.DueAt <= now).ToArray())
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(
            ManualDeadlineTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset dueAt) : ITimer
        {
            private bool _disposed;
            public DateTimeOffset DueAt { get; private set; } = dueAt;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (_disposed) return false;
                DueAt = owner.GetUtcNow() + dueTime;
                return true;
            }

            public void Dispose() => _disposed = true;
            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (_disposed) return;
                _disposed = true;
                callback(state);
            }
        }
    }
}
