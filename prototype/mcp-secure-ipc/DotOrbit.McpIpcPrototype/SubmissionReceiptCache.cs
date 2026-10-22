using System.Security.Cryptography;
using System.Text;

namespace DotOrbit.McpIpcPrototype;

internal sealed class SubmissionReceiptCache
{
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _receipts = new(StringComparer.Ordinal);

    public (string Receipt, bool Duplicate) Add(string idempotencyKey)
    {
        lock (_gate)
        {
            if (_receipts.TryGetValue(idempotencyKey, out var existing))
            {
                return (existing, true);
            }

            if (_receipts.Count >= PrototypeProtocol.MaximumSubmissionReceipts)
            {
                throw new ProtocolException("submission-limit");
            }

            var receipt = CreateReceipt(idempotencyKey);
            _receipts.Add(idempotencyKey, receipt);
            return (receipt, false);
        }
    }

    private static string CreateReceipt(string idempotencyKey)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(idempotencyKey));
        return $"synthetic-{Convert.ToHexStringLower(digest.AsSpan(0, 8))}";
    }
}
