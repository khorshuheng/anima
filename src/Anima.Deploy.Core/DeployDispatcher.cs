using System.Threading.Channels;

namespace Anima.Deploy;

public sealed record DeployWorkItem(DeployRun Run, DeployRule Rule, GitPushEvent Push);

public sealed class DeployDispatcher
{
    private readonly Channel<DeployWorkItem> _channel =
        Channel.CreateUnbounded<DeployWorkItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });

    public ValueTask EnqueueAsync(DeployWorkItem item, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return _channel.Writer.WriteAsync(item, cancellationToken);
    }

    public IAsyncEnumerable<DeployWorkItem> ReadAllAsync(CancellationToken cancellationToken = default)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
