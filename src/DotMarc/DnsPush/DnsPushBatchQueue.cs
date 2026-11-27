using System.Threading.Channels;
using DotMarc.Audit;

namespace DotMarc.DnsPush;

/// <summary>A signed-in batch waiting to be pushed. The session is the provider's in-memory sign-in, which exists nowhere else: it is not stored,
/// logged or serialised, and is dropped when the batch has been pushed.</summary>
public sealed record DnsPushBatchWork(Guid BatchId, string Provider, IDnsPushSession Session, AuditActor Actor);

/// <summary>Hands signed-in batches from the callback to the worker that pushes them, inside this process. Nothing is persisted: a batch the
/// process stops in the middle of is marked Interrupted when it starts again, because its session is gone.</summary>
public sealed class DnsPushBatchQueue
{
    private readonly Channel<DnsPushBatchWork> _channel = Channel.CreateUnbounded<DnsPushBatchWork>(new UnboundedChannelOptions { SingleReader = true });

    public ChannelReader<DnsPushBatchWork> Reader => _channel.Reader;

    public void Enqueue(DnsPushBatchWork work) => _channel.Writer.TryWrite(work);
}
