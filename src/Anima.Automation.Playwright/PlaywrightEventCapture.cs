using System.Collections.Concurrent;
using System.Text;
using Microsoft.Playwright;

namespace Anima.Automation.Playwright;

/// <summary>
/// Bridges Playwright page events into a <see cref="BrowserEventLog"/>. Records
/// metadata for every console message, page error, request, response, failure,
/// main-frame navigation and dialog; headers and bodies only when the session
/// opted in, and never with credential-bearing headers or query parameters.
/// </summary>
internal static class PlaywrightEventCapture
{
    private const int MaxTextChars = 4096;

    /// <summary>Responses above this size are recorded without their body.</summary>
    internal const long MaxCapturedBodyBytes = 256 * 1024;

    /// <summary>Safety valve for requests that never produce a terminal event.</summary>
    private const int MaxTrackedRequests = 1024;

    private static readonly HashSet<string> SecretHeaders =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "authorization",
            "cookie",
            "set-cookie",
            "proxy-authorization",
        };

    public static PageEventCapture Attach(
        IPage page,
        BrowserEventLog log,
        SessionOptions options)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(options);

        var capture = new PageEventCapture();
        var inFlight = new ConcurrentDictionary<IRequest, Exchange>();
        var exchangeCounter = 0;

        page.Console += (_, message) => log.Append(new BrowserEvent
        {
            Kind = BrowserEventKind.Console,
            Level = message.Type,
            Text = Truncate(message.Text),
            Url = BrowserUrlSanitizer.Sanitize(PageUrl(page)),
        });

        page.PageError += (_, error) => log.Append(new BrowserEvent
        {
            Kind = BrowserEventKind.PageError,
            Level = "error",
            Text = Truncate(error),
            Url = BrowserUrlSanitizer.Sanitize(PageUrl(page)),
        });

        page.Request += (_, request) =>
        {
            if (inFlight.Count >= MaxTrackedRequests)
            {
                // Requests that never respond (aborted by navigation, page closed
                // mid-flight) would otherwise accumulate for the session lifetime.
                inFlight.Clear();
            }

            var exchange = new Exchange(
                $"r{Interlocked.Increment(ref exchangeCounter)}",
                DateTimeOffset.UtcNow);

            inFlight[request] = exchange;

            log.Append(new BrowserEvent
            {
                Kind = BrowserEventKind.Request,
                RequestId = exchange.Id,
                Method = request.Method,
                Url = BrowserUrlSanitizer.Sanitize(request.Url),
                ResourceType = request.ResourceType,
                Headers = options.CaptureHeaders ? CaptureHeaders(request.Headers) : null,
                Body = options.CaptureBodies ? Truncate(request.PostData) : null,
            });
        };

        page.Response += (_, response) => RecordResponse(log, capture, response, inFlight, options);

        page.RequestFailed += (_, request) =>
        {
            var failed = Complete(inFlight, request);

            log.Append(new BrowserEvent
            {
                Kind = BrowserEventKind.RequestFailed,
                RequestId = failed.Id,
                Level = "error",
                Method = request.Method,
                Url = BrowserUrlSanitizer.Sanitize(request.Url),
                ResourceType = request.ResourceType,
                Detail = Truncate(request.Failure),
                DurationMs = failed.DurationMs,
            });
        };

        page.FrameNavigated += (_, frame) =>
        {
            // Sub-frames add noise without telling a caller where the page went.
            if (frame.ParentFrame is not null)
            {
                return;
            }

            // Bodies of the previous document are unreadable once a new one commits.
            capture.ClearBodies();

            // A new document means in-flight requests from the old one will never
            // report; drop their timings rather than growing the map.
            inFlight.Clear();

            log.Append(new BrowserEvent
            {
                Kind = BrowserEventKind.Navigation,
                Url = BrowserUrlSanitizer.Sanitize(frame.Url),
                Detail = "committed",
            });
        };

        // Playwright considers a dialog handled once this handler returns, so answer
        // it here: the page stays blocked until DismissAsync/AcceptAsync completes.
        page.Dialog += async (_, dialog) =>
        {
            try
            {
                await HandleDialogAsync(log, dialog, options.DialogBehavior).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // This is an async-void handler: anything escaping here would be an
                // unhandled exception rather than a failed session. Record it.
                log.Append(new BrowserEvent
                {
                    Kind = BrowserEventKind.Dialog,
                    Level = "error",
                    Detail = "answer-failed",
                    Text = Truncate(exception.Message),
                });
            }
        };

        return capture;
    }

    private static void RecordResponse(
        BrowserEventLog log,
        PageEventCapture capture,
        IResponse response,
        ConcurrentDictionary<IRequest, Exchange> inFlight,
        SessionOptions options)
    {
        var request = response.Request;

        if (options.CaptureBodies)
        {
            // BodyAsync is asynchronous and can fail for redirects and 204s, so the
            // response event is recorded once the body attempt settles. Ordering
            // between this and later events is by timestamp, not arrival.
            _ = RecordResponseWithBodyAsync(log, capture, response, inFlight, options);
            return;
        }

        var exchange = Complete(inFlight, request);
        capture.Remember(exchange.Id, response);

        log.Append(new BrowserEvent
        {
            Kind = BrowserEventKind.Response,
            RequestId = exchange.Id,
            Status = response.Status,
            Method = request.Method,
            Url = BrowserUrlSanitizer.Sanitize(response.Url),
            ResourceType = request.ResourceType,
            DurationMs = exchange.DurationMs,
            Headers = options.CaptureHeaders ? CaptureHeaders(response.Headers) : null,
        });
    }

    private static async Task RecordResponseWithBodyAsync(
        BrowserEventLog log,
        PageEventCapture capture,
        IResponse response,
        ConcurrentDictionary<IRequest, Exchange> inFlight,
        SessionOptions options)
    {
        var request = response.Request;
        string? body = null;
        string? bodySkipped = null;

        // BodyAsync has no size limit, so an oversized response would be fully
        // buffered before Truncate cuts it. Skip it when the length is declared.
        var declaredLength = ParseContentLength(response.Headers);
        if (declaredLength > MaxCapturedBodyBytes)
        {
            bodySkipped = $"body-skipped:{declaredLength} bytes";
        }
        else
        {
            try
            {
                body = Truncate(Encoding.UTF8.GetString(await response.BodyAsync().ConfigureAwait(false)));
            }
            catch (PlaywrightException)
            {
                // Redirect chains and empty responses have no body to read.
            }
        }

        var exchange = Complete(inFlight, request);
        capture.Remember(exchange.Id, response);

        log.Append(new BrowserEvent
        {
            Kind = BrowserEventKind.Response,
            RequestId = exchange.Id,
            Status = response.Status,
            Method = request.Method,
            Url = BrowserUrlSanitizer.Sanitize(response.Url),
            ResourceType = request.ResourceType,
            DurationMs = exchange.DurationMs,
            Headers = options.CaptureHeaders ? CaptureHeaders(response.Headers) : null,
            Body = body,
            Detail = bodySkipped,
        });
    }

    private static async Task HandleDialogAsync(
        BrowserEventLog log,
        IDialog dialog,
        BrowserDialogBehavior behavior)
    {
        var accepted = behavior == BrowserDialogBehavior.Accept;

        log.Append(new BrowserEvent
        {
            Kind = BrowserEventKind.Dialog,
            Level = "warning",
            Detail = dialog.Type,
            Text = Truncate(dialog.Message),
            Url = BrowserUrlSanitizer.Sanitize(Truncate(dialog.Page?.Url)),
        });

        try
        {
            if (accepted)
            {
                await dialog.AcceptAsync(dialog.DefaultValue).ConfigureAwait(false);
            }
            else
            {
                await dialog.DismissAsync().ConfigureAwait(false);
            }
        }
        catch (PlaywrightException)
        {
            // The page can disappear between the event and the answer.
        }
    }

    private static Dictionary<string, string> CaptureHeaders(IEnumerable<KeyValuePair<string, string>> headers)
    {
        var captured = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var header in headers)
        {
            captured[header.Key] = SecretHeaders.Contains(header.Key) ? "<redacted>" : header.Value;
        }

        return captured;
    }

    /// <summary>
    /// Removes the exchange record and returns it with its measured duration, or a
    /// synthetic record when the request event was never observed.
    /// </summary>
    private static Exchange Complete(ConcurrentDictionary<IRequest, Exchange> inFlight, IRequest request)
    {
        if (!inFlight.TryRemove(request, out var exchange))
        {
            return new Exchange(Id: null, StartedAt: default);
        }

        return exchange with
        {
            DurationMs = Math.Round((DateTimeOffset.UtcNow - exchange.StartedAt).TotalMilliseconds, 1),
        };
    }

    private readonly record struct Exchange(string? Id, DateTimeOffset StartedAt)
    {
        public double? DurationMs { get; init; }
    }

    private static string? PageUrl(IPage page)
    {
        try
        {
            return page.Url;
        }
        catch (PlaywrightException)
        {
            return null;
        }
    }

    /// <summary>Declared response size, when the server sent one.</summary>
    internal static long? DeclaredLength(IEnumerable<KeyValuePair<string, string>> headers)
        => ParseContentLength(headers);

    private static long? ParseContentLength(IEnumerable<KeyValuePair<string, string>> headers)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, "content-length", StringComparison.OrdinalIgnoreCase)
                && long.TryParse(header.Value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var length))
            {
                return length;
            }
        }

        return null;
    }

    private static string? Truncate(string? value)
        => value is null || value.Length <= MaxTextChars ? value : value[..MaxTextChars];
}

/// <summary>
/// Handle returned by <see cref="PlaywrightEventCapture.Attach"/>. Keeps the
/// most recent responses so a wait can read a body on demand, without forcing
/// every session to capture bodies for every response.
/// </summary>
internal sealed class PageEventCapture
{
    private const int Capacity = 64;

    private readonly Queue<string> _order = new();
    private readonly Dictionary<string, IResponse> _responses = new(StringComparer.Ordinal);
    private readonly object _gate = new();

    public void Remember(string? requestId, IResponse response)
    {
        if (string.IsNullOrEmpty(requestId))
        {
            return;
        }

        lock (_gate)
        {
            if (_responses.TryAdd(requestId, response))
            {
                _order.Enqueue(requestId);
            }

            while (_order.Count > Capacity)
            {
                _responses.Remove(_order.Dequeue());
            }
        }
    }

    public void ClearBodies()
    {
        lock (_gate)
        {
            _order.Clear();
            _responses.Clear();
        }
    }

    /// <summary>
    /// Reads a body after the fact. Applies the same declared-length cap as the
    /// capture path, so an on-demand read cannot buffer what the log refuses to.
    /// </summary>
    public async Task<ResponseBody> ReadBodyAsync(string requestId)
    {
        IResponse? response;

        lock (_gate)
        {
            _responses.TryGetValue(requestId, out response);
        }

        if (response is null)
        {
            return new ResponseBody(null, null);
        }

        if (PlaywrightEventCapture.DeclaredLength(response.Headers) is { } declaredLength
            && declaredLength > PlaywrightEventCapture.MaxCapturedBodyBytes)
        {
            return new ResponseBody(null, declaredLength);
        }

        try
        {
            var bytes = await response.BodyAsync().ConfigureAwait(false);
            return new ResponseBody(System.Text.Encoding.UTF8.GetString(bytes), null);
        }
        catch (PlaywrightException)
        {
            // Redirect chains, 204s and navigated-away documents have no body.
            return new ResponseBody(null, null);
        }
    }
}

/// <summary>Outcome of an on-demand body read: text, or the size that made it too large.</summary>
internal readonly record struct ResponseBody(string? Text, long? SkippedBytes);
