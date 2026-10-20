namespace DotOrbit.McpIpcPrototype.Tests;

public sealed class SubmissionReceiptCacheTests
{
    [Fact]
    public void UniqueReceiptLimitIsBoundedWhileExistingKeysRemainIdempotent()
    {
        var cache = new SubmissionReceiptCache();
        string? firstReceipt = null;
        for (var index = 0; index < PrototypeProtocol.MaximumSubmissionReceipts; index++)
        {
            var result = cache.Add($"key-{index}");
            firstReceipt ??= result.Receipt;
            Assert.False(result.Duplicate);
        }

        var duplicate = cache.Add("key-0");
        var exception = Assert.Throws<ProtocolException>(() => cache.Add("over-limit"));

        Assert.True(duplicate.Duplicate);
        Assert.Equal(firstReceipt, duplicate.Receipt);
        Assert.Equal("submission-limit", exception.Code);
    }

    [Fact]
    public async Task ConcurrentSameKeyAddsExactlyOneReceipt()
    {
        var cache = new SubmissionReceiptCache();
        using var start = new ManualResetEventSlim();
        var calls = Enumerable.Range(0, 8)
            .Select(_ => Task.Run(() =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                return cache.Add("shared-key");
            }, TestContext.Current.CancellationToken))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(calls);

        Assert.Single(results, result => !result.Duplicate);
        Assert.Equal(7, results.Count(result => result.Duplicate));
        Assert.Single(results.Select(result => result.Receipt).Distinct(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ConcurrentSameFinalKeyDoesNotExceedReceiptLimit()
    {
        var cache = new SubmissionReceiptCache();
        for (var index = 0; index < PrototypeProtocol.MaximumSubmissionReceipts - 1; index++)
        {
            cache.Add($"existing-{index}");
        }

        using var start = new ManualResetEventSlim();
        var calls = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait(TestContext.Current.CancellationToken);
                return cache.Add("final-key");
            }, TestContext.Current.CancellationToken))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(calls);

        Assert.Single(results, result => !result.Duplicate);
        Assert.Equal(31, results.Count(result => result.Duplicate));
        Assert.Single(results.Select(result => result.Receipt).Distinct(StringComparer.Ordinal));
        Assert.Equal("submission-limit", Assert.Throws<ProtocolException>(() => cache.Add("over-limit")).Code);
    }
}
