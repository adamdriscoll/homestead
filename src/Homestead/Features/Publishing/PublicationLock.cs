namespace Homestead.Features.Publishing;

// All desired-public mutations and draft/media saves share the single-instance writer lock.
public sealed class PublicationLock
{
    public SemaphoreSlim Semaphore { get; } = new(1, 1);
}
