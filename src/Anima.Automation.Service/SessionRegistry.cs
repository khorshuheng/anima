using System.Collections.Concurrent;

namespace Anima.Automation.Service;

public sealed record SessionRequest(
    string? BaseUrl = null,
    bool? Headless = null,
    double? TimeoutMs = null,
    string? TracePath = null,
    bool? CaptureHeaders = null,
    bool? CaptureBodies = null,
    string? DialogBehavior = null);

public sealed class SessionRegistry : IAsyncDisposable
{
    private readonly IBrowserDriver _driver;
    private readonly ILogger<SessionRegistry> _logger;
    private readonly ConcurrentDictionary<string, Entry> _sessions = new(StringComparer.Ordinal);

    public SessionRegistry(IBrowserDriver driver, ILogger<SessionRegistry> logger)
    {
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(logger);
        _driver = driver;
        _logger = logger;
    }

    public async Task<Entry> StartAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var options = new SessionOptions
        {
            BaseUrl = request.BaseUrl,
            // Headed by default; a caller that wants no window asks for it explicitly.
            Headless = request.Headless ?? false,
            TimeoutMs = request.TimeoutMs ?? 30_000,
            TracePath = request.TracePath,
            CaptureHeaders = request.CaptureHeaders ?? false,
            CaptureBodies = request.CaptureBodies ?? false,
            DialogBehavior = ParseDialogBehavior(request.DialogBehavior),
        };

        var session = await _driver.StartSessionAsync(options, cancellationToken);
        var entry = new Entry(session, options, DateTimeOffset.UtcNow);
        _sessions[session.Id] = entry;
        return entry;
    }

    public Entry? Get(string id)
        => _sessions.TryGetValue(id, out var entry) ? entry : null;

    public IReadOnlyList<Entry> List() => [.. _sessions.Values.OrderBy(entry => entry.StartedAt)];

    private static BrowserDialogBehavior ParseDialogBehavior(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return BrowserDialogBehavior.Dismiss;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "dismiss" => BrowserDialogBehavior.Dismiss,
            "accept" => BrowserDialogBehavior.Accept,
            _ => throw new InvalidOperationException(
                $"Unknown dialogBehavior '{value}'. Use 'dismiss' or 'accept'."),
        };
    }

    public async Task<bool> CloseAsync(string id)
    {
        if (!_sessions.TryRemove(id, out var entry))
        {
            return false;
        }

        await entry.Session.DisposeAsync();
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var entry in _sessions.Values)
        {
            try
            {
                await entry.Session.DisposeAsync();
            }
            catch (Exception exception)
            {
                // Host shutdown must not fail on a broken browser session.
                _logger.LogWarning(exception, "Failed to dispose browser session {SessionId}.", entry.Session.Id);
            }
        }

        _sessions.Clear();
    }

    public sealed record Entry(IBrowserSession Session, SessionOptions Options, DateTimeOffset StartedAt);
}
