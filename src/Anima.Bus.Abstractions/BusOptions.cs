namespace Anima.Bus;

public enum BusFullMode
{
    Wait,
    DropOldest,
    DropNewest,
    DropWrite,
}

public sealed class BusOptions
{
    /// <summary>Per-subscription queue capacity.</summary>
    public int SubscriptionCapacity { get; set; } = 1024;

    /// <summary>What to do when a subscription queue is full.</summary>
    public BusFullMode FullMode { get; set; } = BusFullMode.DropOldest;

    /// <summary>How many times a handler is retried before the message is dead-lettered.</summary>
    public int MaxDeliveryAttempts { get; set; } = 3;

    /// <summary>Delay between delivery attempts.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromMilliseconds(50);

    /// <summary>Publish failed messages to <see cref="BusTopics.DeadLetter"/>.</summary>
    public bool EnableDeadLetter { get; set; } = true;
}
