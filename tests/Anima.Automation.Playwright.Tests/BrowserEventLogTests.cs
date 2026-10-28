namespace Anima.Automation.Playwright.Tests;

public sealed class BrowserEventLogTests
{
    [Fact]
    public void Append_assigns_monotonic_sequences_and_timestamps()
    {
        var log = new BrowserEventLog(10);

        var first = log.Append(new BrowserEvent { Kind = BrowserEventKind.Navigation, Url = "/a" });
        var second = log.Append(new BrowserEvent { Kind = BrowserEventKind.Navigation, Url = "/b" });

        Assert.Equal(1, first.Sequence);
        Assert.Equal(2, second.Sequence);
        Assert.Equal(2, log.LatestSequence);
        Assert.NotEqual(default, first.Timestamp);
    }

    [Fact]
    public void Caller_supplied_sequence_and_timestamp_are_overwritten()
    {
        var log = new BrowserEventLog(10);

        var recorded = log.Append(new BrowserEvent
        {
            Kind = BrowserEventKind.Console,
            Sequence = 99,
            Timestamp = DateTimeOffset.UnixEpoch,
        });

        Assert.Equal(1, recorded.Sequence);
        Assert.NotEqual(DateTimeOffset.UnixEpoch, recorded.Timestamp);
    }

    [Fact]
    public void Read_returns_buffered_events_oldest_first()
    {
        var log = Record(log: new BrowserEventLog(10), count: 3);

        var events = log.Read();

        Assert.Equal([1L, 2L, 3L], events.Select(item => item.Sequence));
    }

    [Fact]
    public void Read_honours_the_cursor_and_limit()
    {
        var log = Record(log: new BrowserEventLog(10), count: 5);

        var events = log.Read(afterSequence: 2, limit: 2);

        Assert.Equal([3L, 4L], events.Select(item => item.Sequence));
    }

    [Fact]
    public void Full_buffer_evicts_the_oldest_and_counts_drops()
    {
        var log = Record(log: new BrowserEventLog(3), count: 5);

        var events = log.Read();

        Assert.Equal([3L, 4L, 5L], events.Select(item => item.Sequence));
        Assert.Equal(2, log.DroppedCount);
        Assert.Equal(3, log.Count);
    }

    [Fact]
    public void Capacity_must_be_positive()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrowserEventLog(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new BrowserEventLog(-1));
    }

    [Fact]
    public async Task Subscribe_replays_the_backlog_then_streams_live_events()
    {
        var log = Record(log: new BrowserEventLog(10), count: 2);

        var received = new List<long>();
        await foreach (var item in log.SubscribeAsync(afterSequence: 0))
        {
            received.Add(item.Sequence);

            if (received.Count == 2)
            {
                // Adding while subscribed must reach the same reader.
                log.Append(new BrowserEvent { Kind = BrowserEventKind.Navigation, Url = "/live" });
            }

            if (received.Count == 3)
            {
                break;
            }
        }

        Assert.Equal([1L, 2L, 3L], received);
    }

    [Fact]
    public async Task Subscribe_skips_events_at_or_before_the_cursor()
    {
        var log = Record(log: new BrowserEventLog(10), count: 4);

        var received = new List<long>();
        await foreach (var item in log.SubscribeAsync(afterSequence: 3))
        {
            received.Add(item.Sequence);

            if (received.Count == 1)
            {
                break;
            }
        }

        Assert.Equal([4L], received);
    }

    [Fact]
    public async Task Subscribe_can_be_cancelled_without_leaking_a_subscriber()
    {
        var log = new BrowserEventLog(10);
        using var cancellation = new CancellationTokenSource();
        int delivered = 0;

        var reader = Task.Run(async () =>
        {
            await foreach (var _ in log.SubscribeAsync(0, cancellation.Token))
            {
                delivered++;
            }
        });

        await Task.Delay(50);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reader);
        Assert.Equal(0, delivered);

        // A cancelled subscriber must not receive later events, and appending after
        // the cancellation must still work.
        var recorded = log.Append(new BrowserEvent { Kind = BrowserEventKind.Console, Text = "after" });
        Assert.Equal(1, recorded.Sequence);
    }

    [Fact]
    public async Task A_slow_subscriber_drops_its_own_backlog_without_blocking_the_log()
    {
        var log = new BrowserEventLog(10);
        var enumeration = log.SubscribeAsync(0).GetAsyncEnumerator();

        try
        {
            // Registers the subscriber and parks it waiting for the first event.
            ValueTask<bool> pending = enumeration.MoveNextAsync();

            // Far more than the subscriber channel capacity: appends must not block.
            for (int index = 0; index < 1_000; index++)
            {
                log.Append(new BrowserEvent { Kind = BrowserEventKind.Console, Text = "line" });
            }

            Assert.Equal(1_000, log.LatestSequence);
            Assert.Equal(990, log.DroppedCount);
            Assert.True(await pending);

            // The subscriber fell behind and lost its oldest events; how many it kept
            // is a Channel implementation detail, so only forward progress is asserted.
            long first = enumeration.Current.Sequence;

            log.Append(new BrowserEvent { Kind = BrowserEventKind.Console, Text = "after" });
            Assert.True(await enumeration.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.True(enumeration.Current.Sequence > first);
        }
        finally
        {
            await enumeration.DisposeAsync();
        }
    }

    [Fact]
    public async Task Subscribe_with_a_cursor_past_the_end_resumes_at_the_next_event()
    {
        // A stale cursor (another session, a persisted dashboard cursor, or a restart,
        // since every session starts at 1) used to silence the stream permanently.
        // The clamp resumes at "now": nothing old is replayed, new events arrive.
        var log = Record(log: new BrowserEventLog(1_000), count: 3);

        using var stop = new CancellationTokenSource();
        var writer = Task.Run(async () =>
        {
            int sequence = 0;
            while (!stop.IsCancellationRequested)
            {
                log.Append(new BrowserEvent { Kind = BrowserEventKind.Console, Text = $"live-{sequence++}" });
                await Task.Delay(10, CancellationToken.None);
            }
        });

        var received = new List<long>();
        try
        {
            await foreach (var item in log.SubscribeAsync(afterSequence: 1_000).WithCancellation(Timeout()))
            {
                received.Add(item.Sequence);
                break;
            }
        }
        finally
        {
            await stop.CancelAsync();
            await writer;
        }

        Assert.NotEmpty(received);

        // The three events recorded before the subscription must not be replayed.
        Assert.All(received, sequence => Assert.True(sequence > 3));
    }

    [Fact]
    public async Task Read_with_a_cursor_past_the_end_returns_nothing_but_keeps_working()
    {
        var log = Record(log: new BrowserEventLog(10), count: 2);

        Assert.Empty(log.Read(afterSequence: 1_000));

        log.Append(new BrowserEvent { Kind = BrowserEventKind.Console, Text = "later" });
        Assert.Equal([3L], log.Read(afterSequence: 2).Select(item => item.Sequence));
    }

    [Fact]
    public async Task Complete_finishes_live_subscribers()
    {
        var log = new BrowserEventLog(10);
        var received = new List<long>();

        var reader = Task.Run(async () =>
        {
            await foreach (var item in log.SubscribeAsync(0).WithCancellation(Timeout()))
            {
                received.Add(item.Sequence);

                if (received.Count == 1)
                {
                    log.Complete();
                }
            }
        });

        log.Append(new BrowserEvent { Kind = BrowserEventKind.Console, Text = "before-close" });

        await reader.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal([1L], received);
    }

    [Fact]
    public async Task Subscribe_after_complete_replays_then_finishes_without_hanging()
    {
        var log = Record(log: new BrowserEventLog(10), count: 2);
        log.Complete();

        var received = new List<long>();
        await foreach (var item in log.SubscribeAsync(0).WithCancellation(Timeout()))
        {
            received.Add(item.Sequence);
        }

        Assert.Equal([1L, 2L], received);
    }

    /// <summary>Fails a stuck stream instead of hanging the test run.</summary>
    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    private static BrowserEventLog Record(BrowserEventLog log, int count)
    {
        for (int index = 0; index < count; index++)
        {
            log.Append(new BrowserEvent { Kind = BrowserEventKind.Navigation, Url = $"/page/{index}" });
        }

        return log;
    }
}
