using System.Collections.Concurrent;
using Google.Protobuf;
using Google.Protobuf.Reflection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Anima.Contracts;

/// <summary>
/// Loads <c>*.pb</c> descriptor sets produced by <c>scripts/compile-contracts.sh</c>.
/// </summary>
public sealed class FileDescriptorSetCatalog : ISchemaCatalog, IDisposable
{
    private readonly SchemaCatalogOptions _options;
    private readonly ILogger<FileDescriptorSetCatalog> _logger;
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, bool> _loadFailures = new(StringComparer.Ordinal);

    private Dictionary<string, MessageDescriptor> _messages = new(StringComparer.Ordinal);
    private FileSystemWatcher? _watcher;
    private Timer? _reloadDebounce;
    private bool _disposed;

    public FileDescriptorSetCatalog(IOptions<SchemaCatalogOptions> options, ILogger<FileDescriptorSetCatalog> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options.Value;
        _logger = logger;

        Reload();

        if (_options.WatchForChanges)
        {
            StartWatcher();
        }
    }

    public IReadOnlyCollection<MessageDescriptor> Messages
    {
        get
        {
            lock (_gate)
            {
                return _messages.Values.ToArray();
            }
        }
    }

    public MessageDescriptor? Find(string fullName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        lock (_gate)
        {
            return _messages.GetValueOrDefault(fullName);
        }
    }

    public bool TryFind(string fullName, out MessageDescriptor descriptor)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        lock (_gate)
        {
            return _messages.TryGetValue(fullName, out descriptor!);
        }
    }

    public Task ReloadAsync(CancellationToken cancellationToken = default)
    {
        Reload();
        return Task.CompletedTask;
    }

    public void Reload()
    {
        var directory = ResolveDirectory();
        var loaded = new Dictionary<string, MessageDescriptor>(StringComparer.Ordinal);

        if (!Directory.Exists(directory))
        {
            _logger.LogWarning(
                "Contract descriptor directory {Directory} does not exist; runtime contracts are empty.",
                directory);
            lock (_gate)
            {
                _messages = loaded;
            }
            return;
        }

        foreach (var path in Directory.EnumerateFiles(directory, _options.FilePattern, SearchOption.TopDirectoryOnly)
                     .Order(StringComparer.Ordinal))
        {
            try
            {
                var descriptorSet = FileDescriptorSet.Parser.ParseFrom(File.ReadAllBytes(path));
                var files = FileDescriptor.BuildFromByteStrings(
                    descriptorSet.File.Select(file => file.ToByteString()));

                foreach (var file in files)
                {
                    Collect(file, loaded);
                }

                _loadFailures.TryRemove(path, out _);
            }
            catch (Exception ex) when (ex is InvalidProtocolBufferException or InvalidOperationException or IOException)
            {
                // Log the first failure per file, not on every watcher tick.
                if (_loadFailures.TryAdd(path, true))
                {
                    _logger.LogError(ex, "Failed to load contract descriptor set {Path}.", path);
                }
            }
        }

        lock (_gate)
        {
            _messages = loaded;
        }

        _logger.LogInformation(
            "Loaded {Count} contract messages from {Directory}.",
            loaded.Count,
            directory);
    }

    private static void Collect(FileDescriptor file, Dictionary<string, MessageDescriptor> messages)
    {
        ArgumentNullException.ThrowIfNull(file);
        foreach (var message in file.MessageTypes)
        {
            Collect(message, messages);
        }
    }

    private static void Collect(MessageDescriptor descriptor, Dictionary<string, MessageDescriptor> messages)
    {
        messages[descriptor.FullName] = descriptor;
        foreach (var nested in descriptor.NestedTypes)
        {
            Collect(nested, messages);
        }
    }

    private string ResolveDirectory()
    {
        var configured = _options.DescriptorDirectory;
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = "contracts/descriptors";
        }

        return Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), configured));
    }

    private void StartWatcher()
    {
        var directory = ResolveDirectory();
        if (!Directory.Exists(directory))
        {
            _logger.LogWarning("Cannot watch {Directory}: it does not exist.", directory);
            return;
        }

        _watcher = new FileSystemWatcher(directory, _options.FilePattern)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnDescriptorChanged;
        _watcher.Created += OnDescriptorChanged;
        _watcher.Renamed += OnDescriptorChanged;
        _watcher.Deleted += OnDescriptorChanged;
    }

    private void OnDescriptorChanged(object sender, FileSystemEventArgs args)
    {
        // Debounce: protoc writes a file in several steps.
        _reloadDebounce?.Dispose();
        _reloadDebounce = new Timer(
            _ =>
            {
                _logger.LogInformation("Contract descriptors changed; reloading.");
                Reload();
            },
            state: null,
            dueTime: TimeSpan.FromMilliseconds(250),
            period: Timeout.InfiniteTimeSpan);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _reloadDebounce?.Dispose();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnDescriptorChanged;
            _watcher.Created -= OnDescriptorChanged;
            _watcher.Renamed -= OnDescriptorChanged;
            _watcher.Deleted -= OnDescriptorChanged;
            _watcher.Dispose();
        }
    }
}
