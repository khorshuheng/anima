namespace Anima.Bus;

public interface IBusPublisher
{
    ValueTask PublishAsync(MessageEnvelope envelope, CancellationToken cancellationToken = default);
}
