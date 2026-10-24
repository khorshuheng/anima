namespace Anima.Bus.Client;

public sealed class BusClientOptions
{
    public string BaseUrl { get; set; } = "http://127.0.0.1:7412";

    /// <summary>Reconnect delay after an SSE stream ends unexpectedly.</summary>
    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Per-subscription local queue capacity.</summary>
    public int SubscriptionCapacity { get; set; } = 1024;

    /// <summary>Upper bound for a publish POST. SSE streams are not affected.</summary>
    public TimeSpan PublishTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>How long SubscribeAsync waits for the server's SSE ready frame.</summary>
    public TimeSpan SubscriptionReadyTimeout { get; set; } = TimeSpan.FromSeconds(10);
}
