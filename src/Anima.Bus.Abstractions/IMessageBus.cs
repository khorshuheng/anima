namespace Anima.Bus;

/// <summary>In-process bus surface: publish and subscribe.</summary>
public interface IMessageBus : IBusPublisher, IBusSubscriber;
