using System.Threading.RateLimiting;

namespace LabGuard.Server.ClientUpdates;

// Limits installer uploads per administrator. It runs inside the upload handler, after sign-in, so a request without a
// valid session never uses up anyone's allowance.
public sealed class ClientUploadLimiter : IDisposable
{
    private readonly PartitionedRateLimiter<string> _limiter = PartitionedRateLimiter.Create<string, string>(
        administrator => RateLimitPartition.GetFixedWindowLimiter(administrator, _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = ClientUpdateEndpoints.MaxUploadsPerHour,
            Window = TimeSpan.FromHours(1),
            QueueLimit = 0,
        }));

    // True when this administrator still has an upload left this hour. The lease is released at once: only the count matters.
    public bool TryAcquire(string administrator)
    {
        using var lease = _limiter.AttemptAcquire(administrator);
        return lease.IsAcquired;
    }

    public void Dispose() => _limiter.Dispose();
}
