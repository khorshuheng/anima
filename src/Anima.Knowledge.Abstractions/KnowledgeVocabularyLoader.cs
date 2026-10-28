using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Anima.Knowledge;

/// <summary>
/// The resolved, immutable vocabulary: core entries plus every merged pack. Built
/// once at startup and injected into the graph stores, so stores no longer read the
/// compile-time definitions.
/// </summary>
public sealed class KnowledgeVocabularyCatalog
{
    /// <summary>Endpoint wildcard: the edge type accepts any kind on that side.</summary>
    public const string Wildcard = "*";

    /// <summary>Lowest id a pack may claim. Core ids stay below this floor forever.</summary>
    public const int PackIdFloor = 1000;

    /// <summary>Name of the embedded pack; also the <c>source</c> of core entries.</summary>
    public const string CorePack = "core";

    private readonly Dictionary<string, NodeKindDefinition> _kinds =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, EdgeTypeDefinition> _edgeTypes =
        new(StringComparer.OrdinalIgnoreCase);

    internal KnowledgeVocabularyCatalog(
        IReadOnlyList<NodeKindDefinition> nodeKinds,
        IReadOnlyList<EdgeTypeDefinition> edgeTypes)
    {
        NodeKinds = nodeKinds;
        EdgeTypes = edgeTypes;
        foreach (var kind in nodeKinds)
        {
            _kinds[kind.Name] = kind;
        }

        foreach (var edgeType in edgeTypes)
        {
            _edgeTypes[edgeType.Name] = edgeType;
        }
    }

    public IReadOnlyList<NodeKindDefinition> NodeKinds { get; }

    public IReadOnlyList<EdgeTypeDefinition> EdgeTypes { get; }

    public bool TryGetKind(string name, [NotNullWhen(true)] out NodeKindDefinition? kind)
        => _kinds.TryGetValue(KnowledgeVocabularyLoader.NormalizeName(name), out kind);

    public NodeKindDefinition RequireKind(string name)
        => TryGetKind(name, out var kind)
            ? kind
            : throw new KnowledgeValidationException($"unknown node kind '{name}'");

    public bool TryGetEdgeType(string name, [NotNullWhen(true)] out EdgeTypeDefinition? edgeType)
        => _edgeTypes.TryGetValue(KnowledgeVocabularyLoader.NormalizeName(name), out edgeType);

    /// <summary>
    /// Resolves an edge type name, accepting either the stored name or the declared
    /// <c>inverse_name</c>. The resolution's <c>Reversed</c> flag is true when the
    /// caller asked for the relation from the opposite end, so its source and
    /// destination roles are swapped relative to the stored edge.
    /// </summary>
    public bool TryResolveEdgeType(string name, out EdgeTypeResolution resolution)
    {
        string normalized = KnowledgeVocabularyLoader.NormalizeName(name);
        if (_edgeTypes.TryGetValue(normalized, out var direct))
        {
            resolution = new EdgeTypeResolution(direct, Reversed: false);
            return true;
        }

        foreach (var candidate in EdgeTypes)
        {
            if (candidate.InverseName is { } inverse &&
                string.Equals(inverse, normalized, StringComparison.OrdinalIgnoreCase))
            {
                resolution = new EdgeTypeResolution(candidate, Reversed: true);
                return true;
            }
        }

        resolution = default;
        return false;
    }

    /// <summary>Canonical name for a stored or inverse edge type name.</summary>
    public bool TryGetCanonicalEdgeTypeName(string name, [NotNullWhen(true)] out string? canonical)
    {
        if (TryResolveEdgeType(name, out var resolution))
        {
            canonical = resolution.Definition.Name;
            return true;
        }

        canonical = null;
        return false;
    }

    public EdgeTypeDefinition RequireEdgeType(string name)
        => TryGetEdgeType(name, out var edgeType)
            ? edgeType
            : throw new KnowledgeValidationException($"unknown edge type '{name}'");

    /// <summary>True when the edge type permits an edge from <paramref name="sourceKind"/> to <paramref name="destinationKind"/>.</summary>
    public bool AllowsPair(string edgeType, string sourceKind, string destinationKind)
    {
        if (!TryGetEdgeType(edgeType, out var definition))
        {
            return false;
        }

        string source = KnowledgeVocabularyLoader.NormalizeName(sourceKind);
        string destination = KnowledgeVocabularyLoader.NormalizeName(destinationKind);
        return definition.Pairs.Any(pair =>
            Matches(pair.Source, source) && Matches(pair.Destination, destination));

        static bool Matches(string expected, string actual)
            => expected == Wildcard || string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the relation may not form a cycle. Traversal and insertion checks
    /// read this instead of comparing edge names.
    /// </summary>
    public bool IsAcyclic(string edgeType)
        => TryGetEdgeType(edgeType, out var definition) && definition.Acyclic;

    /// <summary>Node kinds carrying <paramref name="trait"/>, ordered by id.</summary>
    public IReadOnlyList<NodeKindDefinition> NodeKindsWithTrait(string trait)
        => [.. NodeKinds
            .Where(kind => kind.Traits.Contains(trait, StringComparer.OrdinalIgnoreCase))
            .OrderBy(kind => kind.Id)];

    /// <summary>Edge types carrying <paramref name="trait"/>, ordered by id.</summary>
    public IReadOnlyList<EdgeTypeDefinition> EdgeTypesWithTrait(string trait)
        => [.. EdgeTypes
            .Where(edgeType => edgeType.Traits.Contains(trait, StringComparer.OrdinalIgnoreCase))
            .OrderBy(edgeType => edgeType.Id)];
}

/// <summary>
/// Required-property validation shared by the stores, so a required key declared by a
/// pack fails with the same message whichever provider is configured.
/// </summary>
public static class KnowledgePropertyRules
{
    /// <summary>
    /// Throws when a key in <see cref="NodeKindDefinition.RequiredProps"/> is missing or
    /// empty. Property keys are compared case-insensitively because packs are authored
    /// by hand and property storage is ordinal.
    /// </summary>
    public static void EnsureRequired(
        NodeKindDefinition kind,
        IReadOnlyDictionary<string, string>? properties)
    {
        ArgumentNullException.ThrowIfNull(kind);
        if (kind.RequiredProps.Count == 0)
        {
            return;
        }

        var present = new HashSet<string>(
            (properties ?? new Dictionary<string, string>(StringComparer.Ordinal))
                .Where(pair => !string.IsNullOrWhiteSpace(pair.Value))
                .Select(pair => pair.Key),
            StringComparer.OrdinalIgnoreCase);

        string[] missing = [.. kind.RequiredProps.Where(required => !present.Contains(required))];
        if (missing.Length > 0)
        {
            throw new KnowledgeValidationException(
                $"node kind '{kind.Name}' requires properties {string.Join(", ", missing)}");
        }
    }
}

/// <summary>
/// An edge type resolved from a requested name. <paramref name="Reversed"/> means the
/// caller named the inverse relation, so source and destination roles are swapped.
/// </summary>
public readonly record struct EdgeTypeResolution(EdgeTypeDefinition Definition, bool Reversed);

/// <summary>
/// Loads vocabulary packs. The core pack is an embedded resource of this assembly;
/// user packs are the same schema read from disk and merged additively.
/// </summary>
public static class KnowledgeVocabularyLoader
{
    private const string CoreResourceSuffix = "KnowledgeVocabulary.core.json";
    private const int PackIdBand = 0xFFFF;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new EdgeKindPairJsonConverter() },
    };

    /// <summary>Loads the embedded core vocabulary.</summary>
    public static KnowledgeVocabularyCatalog LoadCore() => Build(LoadEmbeddedCore());

    /// <summary>
    /// Loads the embedded core vocabulary plus the given pack files, applied in the
    /// order supplied. In <see cref="VocabularyMode.Strict"/> a missing file or invalid
    /// pack fails startup; in <see cref="VocabularyMode.Warn"/> the offending pack is
    /// reported through <paramref name="onWarning"/> and skipped, and the remaining
    /// packs are still validated against each other.
    /// </summary>
    public static KnowledgeVocabularyCatalog Load(
        IEnumerable<string>? packPaths,
        VocabularyMode mode = VocabularyMode.Strict,
        Action<string>? onWarning = null)
        => LoadWithPacks(packPaths, mode, onWarning).Catalog;

    /// <summary>
    /// Same as <see cref="Load"/> but also returns the accepted packs, so a caller can
    /// validate a candidate pack on top of the vocabulary that is actually loaded
    /// (a pack may widen another pack's entry, not only a core one).
    /// </summary>
    public static KnowledgeVocabularyLoad LoadWithPacks(
        IEnumerable<string>? packPaths,
        VocabularyMode mode = VocabularyMode.Strict,
        Action<string>? onWarning = null)
    {
        var core = LoadEmbeddedCore();
        var accepted = new List<VocabularyPack>();
        foreach (string path in packPaths ?? [])
        {
            VocabularyPack pack;
            try
            {
                pack = LoadPack(path);
            }
            catch (KnowledgeVocabularyException exception)
            {
                Reject(mode, onWarning, exception.Message);
                continue;
            }

            try
            {
                // Validate incrementally so a bad pack is attributed to its own file and
                // a skipped pack cannot hide a later conflict.
                _ = Build(core, [.. accepted, pack]);
            }
            catch (KnowledgeVocabularyException exception)
            {
                Reject(mode, onWarning, $"{path}: {exception.Message}");
                continue;
            }

            accepted.Add(pack);
        }

        return new KnowledgeVocabularyLoad(Build(core, accepted), accepted);
    }

    private static void Reject(VocabularyMode mode, Action<string>? onWarning, string message)
    {
        if (mode == VocabularyMode.Strict)
        {
            throw new KnowledgeVocabularyException(message);
        }

        onWarning?.Invoke(message);
    }

    public static VocabularyPack LoadEmbeddedCore()
    {
        var assembly = typeof(KnowledgeVocabularyLoader).Assembly;
        string resource = assembly.GetManifestResourceNames()
            .SingleOrDefault(name => name.EndsWith(CoreResourceSuffix, StringComparison.Ordinal))
            ?? throw new KnowledgeVocabularyException($"{CoreResourceSuffix} is not embedded in {assembly.GetName().Name}");
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new KnowledgeVocabularyException($"missing embedded resource {resource}");
        using var reader = new StreamReader(stream);
        return Parse(reader.ReadToEnd(), "core");
    }

    /// <summary>
    /// Resolves the pack files to load, in application order: the repo
    /// <c>config/knowledge</c> directory, the user <c>~/.config/anima/knowledge</c>
    /// directory, then the configured entries. Missing default directories are
    /// ignored; a missing explicit file or directory is an error at load time.
    /// Each entry may be a file, a directory (all <c>*.json</c> inside) or a glob
    /// such as <c>dir/*.json</c>.
    /// </summary>
    public static IReadOnlyList<string> DiscoverPackFiles(
        IEnumerable<string>? configured = null,
        string? repoRoot = null,
        string? userConfigRoot = null)
    {
        var files = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(repoRoot))
        {
            AddDirectory(files, seen, Path.Combine(repoRoot, "config", "knowledge"));
        }

        string userRoot = userConfigRoot ?? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        if (string.IsNullOrWhiteSpace(userRoot))
        {
            userRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".config");
        }

        if (!string.IsNullOrWhiteSpace(userRoot))
        {
            AddDirectory(files, seen, Path.Combine(userRoot, "anima", "knowledge"));
        }

        foreach (string entry in configured ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            AddEntry(files, seen, entry);
        }

        return files;
    }

    private static void AddEntry(List<string> files, HashSet<string> seen, string entry)
    {
        string expanded = Environment.ExpandEnvironmentVariables(entry.Trim());
        if (Directory.Exists(expanded))
        {
            AddDirectory(files, seen, expanded);
            return;
        }

        string? directory = Path.GetDirectoryName(expanded);
        if (!string.IsNullOrWhiteSpace(directory) && !File.Exists(expanded) && Directory.Exists(directory))
        {
            foreach (string? match in Directory.EnumerateFiles(directory, Path.GetFileName(expanded)).Order(StringComparer.Ordinal))
            {
                AddFile(files, seen, match);
            }

            return;
        }

        // Missing explicit paths surface from LoadPack with the path in the message.
        AddFile(files, seen, Path.GetFullPath(expanded));
    }

    private static void AddDirectory(List<string> files, HashSet<string> seen, string directory)
    {
        if (!Directory.Exists(directory))
        {
            return;
        }

        foreach (string? match in Directory.EnumerateFiles(directory, "*.json").Order(StringComparer.Ordinal))
        {
            AddFile(files, seen, match);
        }
    }

    private static void AddFile(List<string> files, HashSet<string> seen, string path)
    {
        string full = Path.GetFullPath(path);
        if (seen.Add(full))
        {
            files.Add(full);
        }
    }

    public static VocabularyPack LoadPack(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new KnowledgeVocabularyException($"vocabulary pack '{path}' does not exist");
        }

        return Parse(File.ReadAllText(path), path);
    }

    public static VocabularyPack Parse(string json, string source)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        try
        {
            return JsonSerializer.Deserialize<VocabularyPack>(json, Json)
                ?? throw new KnowledgeVocabularyException($"{source}: pack document is empty");
        }
        catch (JsonException exception)
        {
            throw new KnowledgeVocabularyException($"{source}: {exception.Message}");
        }
    }

    /// <summary>
    /// Merges the core pack and any user packs. Node kinds are additive only; an edge
    /// type may widen the core endpoint pairs but may not narrow them or change the
    /// core-owned flags.
    /// </summary>
    public static KnowledgeVocabularyCatalog Build(
        VocabularyPack core,
        IEnumerable<VocabularyPack>? packs = null)
    {
        ArgumentNullException.ThrowIfNull(core);

        var kinds = new List<NodeKindDefinition>();
        var edges = new List<EdgeTypeDefinition>();
        var edgeOwner = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var kindIds = new HashSet<int>();
        var edgeIds = new HashSet<int>();

        var packNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pack in Enumerate(core, packs))
        {
            bool isCore = ReferenceEquals(pack, core);
            ValidateHeader(pack, isCore);
            if (!packNames.Add(pack.Name))
            {
                throw new KnowledgeVocabularyException(
                    $"pack '{pack.Name}' is loaded more than once; candidate packs cannot replace a loaded pack");
            }

            foreach (var spec in pack.NodeKinds)
            {
                string name = NormalizeName(spec.Name);
                if (name.Length == 0)
                {
                    throw new KnowledgeVocabularyException($"{pack.Name}: node kind name must not be empty");
                }

                if (kinds.Any(kind => string.Equals(kind.Name, name, StringComparison.OrdinalIgnoreCase)))
                {
                    throw new KnowledgeVocabularyException(
                        $"{pack.Name}: node kind '{name}' is already defined; packs may only add new kinds");
                }

                if (!isCore)
                {
                    EnsurePackPrefix(pack.Name, name, "node kind");
                }

                kinds.Add(new NodeKindDefinition(AssignId(spec.Id, name, pack, isCore, kindIds), name, spec.Description)
                {
                    RequiredProps = NormalizeNames(spec.RequiredProps),
                    Traits = NormalizeNames(spec.Traits),
                    Source = pack.Name,
                    Deprecated = spec.Deprecated,
                });
            }

            foreach (var spec in pack.EdgeTypes)
            {
                string name = NormalizeName(spec.Name);
                if (name.Length == 0)
                {
                    throw new KnowledgeVocabularyException($"{pack.Name}: edge type name must not be empty");
                }

                var pairs = NormalizePairs(spec, pack, name);
                if (edgeOwner.TryGetValue(name, out string? owner))
                {
                    // Widening names an existing entry, so it keeps that entry's name.
                    Widen(edges, edgeIds, pack, spec, name, owner, pairs);
                    continue;
                }

                if (!isCore)
                {
                    EnsurePackPrefix(pack.Name, name, "edge type");
                }

                edgeOwner[name] = pack.Name;
                edges.Add(CreateDefinition(AssignId(spec.Id, name, pack, isCore, edgeIds), name, spec, pairs, pack.Name));
            }
        }

        ValidateReferences(kinds, edges);
        ValidateInverses(edges);
        return new KnowledgeVocabularyCatalog(kinds, edges);
    }

    private static IEnumerable<VocabularyPack> Enumerate(VocabularyPack core, IEnumerable<VocabularyPack>? packs)
    {
        yield return core;
        if (packs is null)
        {
            yield break;
        }

        foreach (var pack in packs.Where(pack => pack is not null))
        {
            yield return pack;
        }
    }

    /// <summary>
    /// A pack may only add names under its own namespace, so a future core entry can
    /// never collide with a pack entry and vice versa.
    /// </summary>
    private static void EnsurePackPrefix(string packName, string name, string kind)
    {
        string prefix = NormalizeName(packName) + ":";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || name.Length == prefix.Length)
        {
            throw new KnowledgeVocabularyException(
                $"{packName}: {kind} '{name}' must be namespaced as '{prefix}<name>' (for example '{prefix}{name}')");
        }
    }

    private static void ValidateHeader(VocabularyPack pack, bool isCore)
    {
        if (pack.SchemaVersion != VocabularyPack.CurrentSchemaVersion)
        {
            throw new KnowledgeVocabularyException(
                $"{pack.Name}: unsupported schema_version {pack.SchemaVersion.ToString(CultureInfo.InvariantCulture)}; expected {VocabularyPack.CurrentSchemaVersion.ToString(CultureInfo.InvariantCulture)}");
        }

        if (string.IsNullOrWhiteSpace(pack.Name))
        {
            throw new KnowledgeVocabularyException("vocabulary pack name must not be empty");
        }

        if (isCore && !string.Equals(pack.Name, "core", StringComparison.OrdinalIgnoreCase))
        {
            throw new KnowledgeVocabularyException($"embedded vocabulary pack must be named 'core', got '{pack.Name}'");
        }

        if (!isCore && string.Equals(pack.Name, "core", StringComparison.OrdinalIgnoreCase))
        {
            throw new KnowledgeVocabularyException("user packs must not be named 'core'");
        }
    }

    private static int AssignId(int? requested, string name, VocabularyPack pack, bool isCore, HashSet<int> usedIds)
    {
        if (requested is { } explicitId)
        {
            if (explicitId <= 0)
            {
                throw new KnowledgeVocabularyException($"{pack.Name}: '{name}' id must be positive, got {explicitId.ToString(CultureInfo.InvariantCulture)}");
            }

            if (isCore && explicitId >= KnowledgeVocabularyCatalog.PackIdFloor)
            {
                throw new KnowledgeVocabularyException(
                    $"{pack.Name}: core entry '{name}' must use an id below {KnowledgeVocabularyCatalog.PackIdFloor.ToString(CultureInfo.InvariantCulture)}, got {explicitId.ToString(CultureInfo.InvariantCulture)}");
            }

            if (!isCore && explicitId < KnowledgeVocabularyCatalog.PackIdFloor)
            {
                throw new KnowledgeVocabularyException(
                    $"{pack.Name}: pack entry '{name}' must use an id of {KnowledgeVocabularyCatalog.PackIdFloor.ToString(CultureInfo.InvariantCulture)} or above, got {explicitId.ToString(CultureInfo.InvariantCulture)}");
            }

            if (!usedIds.Add(explicitId))
            {
                throw new KnowledgeVocabularyException(
                    $"{pack.Name}: id {explicitId.ToString(CultureInfo.InvariantCulture)} for '{name}' is already in use");
            }

            return explicitId;
        }

        if (isCore)
        {
            throw new KnowledgeVocabularyException($"{pack.Name}: core entry '{name}' must declare an explicit id");
        }

        return AllocateId(name, usedIds);
    }

    /// <summary>
    /// Deterministic id for a pack-band entry (pack kind, pack edge type or ad-hoc
    /// node kind). Using one allocator everywhere is what keeps the in-memory store
    /// and SQLite reporting the same id for the same name.
    /// </summary>
    public static int AllocatePackId(string name, IEnumerable<int> usedIds)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(usedIds);
        return AllocateId(NormalizeName(name), [.. usedIds]);
    }

    /// <summary>
    /// Deterministic id for pack entries that omit one, so the in-memory store and
    /// SQLite agree without a database round-trip.
    /// </summary>
    private static int AllocateId(string name, HashSet<int> usedIds)
    {
        uint hash = 2166136261u;
        foreach (char character in name)
        {
            hash ^= char.ToLowerInvariant(character);
            hash *= 16777619u;
        }

        int offset = (int)(hash % PackIdBand);
        for (int attempt = 0; attempt < PackIdBand; attempt++)
        {
            int candidate = KnowledgeVocabularyCatalog.PackIdFloor + ((offset + attempt) % PackIdBand);
            if (usedIds.Add(candidate))
            {
                return candidate;
            }
        }

        throw new KnowledgeVocabularyException($"pack id band is exhausted for '{name}'");
    }

    private static List<EdgeKindPair> NormalizePairs(EdgeTypeSpec spec, VocabularyPack pack, string name)
    {
        var sources = NormalizeNames(spec.SourceKinds);
        var destinations = NormalizeNames(spec.DestinationKinds);
        if (spec.Pairs.Count > 0)
        {
            if (sources.Count > 0 || destinations.Count > 0)
            {
                throw new KnowledgeVocabularyException(
                    $"{pack.Name}: edge type '{name}' must use either pairs or source_kinds/destination_kinds, not both");
            }

            var explicitPairs = new List<EdgeKindPair>(spec.Pairs.Count);
            foreach (var pair in spec.Pairs)
            {
                explicitPairs.Add(new EdgeKindPair(
                    NormalizeEndpoint(pair.Source, pack, name),
                    NormalizeEndpoint(pair.Destination, pack, name)));
            }

            return [.. explicitPairs.Distinct()];
        }

        IReadOnlyList<string> sourceKinds = sources.Count == 0 ? [KnowledgeVocabularyCatalog.Wildcard] : sources;
        IReadOnlyList<string> destinationKinds = destinations.Count == 0 ? [KnowledgeVocabularyCatalog.Wildcard] : destinations;

        var crossProduct = new List<EdgeKindPair>();
        foreach (string source in sourceKinds)
        {
            foreach (string destination in destinationKinds)
            {
                crossProduct.Add(new EdgeKindPair(source, destination));
            }
        }

        return crossProduct;
    }

    private static string NormalizeEndpoint(string value, VocabularyPack pack, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new KnowledgeVocabularyException($"{pack.Name}: edge type '{name}' has an empty kind name in its endpoint pairs");
        }

        return value.Trim() == KnowledgeVocabularyCatalog.Wildcard
            ? KnowledgeVocabularyCatalog.Wildcard
            : NormalizeName(value);
    }

    private static EdgeTypeDefinition CreateDefinition(
        int id,
        string name,
        EdgeTypeSpec spec,
        IReadOnlyList<EdgeKindPair> pairs,
        string packName)
        => new(
            id,
            name,
            spec.Description,
            DeriveEndpointKinds(pairs, pair => pair.Source),
            DeriveEndpointKinds(pairs, pair => pair.Destination),
            spec.Resolves ?? false,
            spec.SingleOutgoing ?? false)
        {
            Pairs = pairs,
            InverseName = ResolveInverse(spec, name, packName),
            Symmetric = spec.Symmetric ?? false,
            Acyclic = spec.Acyclic ?? false,
            Traits = NormalizeNames(spec.Traits),
            Source = packName,
            Deprecated = spec.Deprecated ?? false,
        };

    private static string? ResolveInverse(EdgeTypeSpec spec, string name, string packName)
    {
        string? inverse = NormalizeOptional(spec.InverseName);
        if (spec.Symmetric != true)
        {
            return inverse;
        }

        if (inverse is not null && !string.Equals(inverse, name, StringComparison.OrdinalIgnoreCase))
        {
            throw new KnowledgeVocabularyException(
                $"{packName}: symmetric edge type '{name}' must declare itself as inverse_name, got '{inverse}'");
        }

        return name;
    }

    /// <summary>Distinct endpoint kinds, or an empty set when a pair is a wildcard.</summary>
    private static HashSet<string> DeriveEndpointKinds(
        IReadOnlyList<EdgeKindPair> pairs,
        Func<EdgeKindPair, string> selector)
    {
        var kinds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in pairs)
        {
            string endpoint = selector(pair);
            if (endpoint == KnowledgeVocabularyCatalog.Wildcard)
            {
                return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }

            kinds.Add(endpoint);
        }

        return kinds;
    }

    private static void Widen(
        List<EdgeTypeDefinition> edges,
        HashSet<int> usedIds,
        VocabularyPack pack,
        EdgeTypeSpec spec,
        string name,
        string owner,
        IReadOnlyList<EdgeKindPair> pairs)
    {
        if (string.Equals(owner, pack.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new KnowledgeVocabularyException($"{pack.Name}: edge type '{name}' is defined twice in the same pack");
        }

        int index = edges.FindIndex(edge => string.Equals(edge.Name, name, StringComparison.OrdinalIgnoreCase));
        var existing = edges[index];
        // Every core-owned field is inherited when the widening entry omits it, so a
        // pack can widen pairs without restating flags, traits or the inverse name.
        var declaredTraits = NormalizeNames(spec.Traits);
        if ((spec.Resolves is { } resolves && resolves != existing.Resolves) ||
            (spec.SingleOutgoing is { } single && single != existing.SingleOutgoing) ||
            (spec.Acyclic is { } acyclic && acyclic != existing.Acyclic) ||
            (spec.Symmetric is { } symmetric && symmetric != existing.Symmetric) ||
            (NormalizeOptional(spec.InverseName) is { } inverse &&
             !string.Equals(inverse, existing.InverseName, StringComparison.OrdinalIgnoreCase)) ||
            (declaredTraits.Count > 0 && !existing.Traits.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(declaredTraits)))
        {
            throw new KnowledgeVocabularyException(
                $"{pack.Name}: edge type '{name}' may only widen '{owner}'; resolves, single_outgoing, acyclic, symmetric, inverse_name and traits are fixed by the defining pack");
        }

        if (spec.Id is { } declared && (!usedIds.Contains(declared) || declared != existing.Id))
        {
            throw new KnowledgeVocabularyException(
                $"{pack.Name}: edge type '{name}' widens '{owner}' at id {existing.Id.ToString(CultureInfo.InvariantCulture)}; declaring id {declared.ToString(CultureInfo.InvariantCulture)} is not allowed");
        }

        var merged = existing.Pairs.ToList();
        bool widened = false;
        foreach (var pair in pairs)
        {
            if (merged.Contains(pair))
            {
                continue;
            }

            merged.Add(pair);
            widened = true;
        }

        if (!widened)
        {
            return;
        }

        edges[index] = existing with
        {
            Description = spec.Description.Length > 0 ? spec.Description : existing.Description,
            SourceKinds = DeriveEndpointKinds(merged, pair => pair.Source),
            DestinationKinds = DeriveEndpointKinds(merged, pair => pair.Destination),
            Pairs = merged,
        };
    }

    private static void ValidateReferences(List<NodeKindDefinition> kinds, List<EdgeTypeDefinition> edges)
    {
        var known = new HashSet<string>(kinds.Select(kind => kind.Name), StringComparer.OrdinalIgnoreCase);
        foreach (var edge in edges)
        {
            foreach (var pair in edge.Pairs)
            {
                foreach (string? endpoint in new[] { pair.Source, pair.Destination })
                {
                    if (endpoint != KnowledgeVocabularyCatalog.Wildcard && !known.Contains(endpoint))
                    {
                        throw new KnowledgeVocabularyException(
                            $"edge type '{edge.Name}' references undefined node kind '{endpoint}'");
                    }
                }
            }
        }
    }

    private static void ValidateInverses(List<EdgeTypeDefinition> edges)
    {
        var byName = edges.ToDictionary(edge => edge.Name, StringComparer.OrdinalIgnoreCase);
        var claimed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var edge in edges)
        {
            if (edge.Symmetric && edge.SingleOutgoing)
            {
                throw new KnowledgeVocabularyException(
                    $"symmetric edge type '{edge.Name}' cannot also be single_outgoing: both endpoints are interchangeable");
            }

            if (edge.InverseName is null)
            {
                continue;
            }

            if (!string.Equals(edge.Source, KnowledgeVocabularyCatalog.CorePack, StringComparison.OrdinalIgnoreCase))
            {
                EnsurePackPrefix(edge.Source, edge.InverseName, "inverse_name");
            }

            if (!edge.Symmetric)
            {
                // The inverse is a virtual relation: the same stored edge read backwards.
                // Defining it as a second real type would give the graph two edges for one
                // fact, which the read layer could not tell apart.
                if (byName.TryGetValue(edge.InverseName, out var shadowed))
                {
                    throw new KnowledgeVocabularyException(
                        $"edge type '{edge.Name}' declares inverse_name '{edge.InverseName}', which is also a defined edge type ('{shadowed.Name}'); define the relation once");
                }

                if (claimed.TryGetValue(edge.InverseName, out string? owner))
                {
                    throw new KnowledgeVocabularyException(
                        $"edge types '{owner}' and '{edge.Name}' both claim inverse_name '{edge.InverseName}'");
                }

                claimed[edge.InverseName] = edge.Name;
                continue;
            }

            var mirrored = new HashSet<EdgeKindPair>();
            foreach (var pair in edge.Pairs)
            {
                string source = pair.Destination;
                string destination = pair.Source;
                mirrored.Add(new EdgeKindPair(source, destination));
            }

            EdgeKindPair[] missing = [.. mirrored.Where(pair => !edge.Pairs.Contains(pair))];
            if (missing.Length > 0)
            {
                throw new KnowledgeVocabularyException(
                    $"symmetric edge type '{edge.Name}' does not allow the mirrored pair ({missing[0].Source}, {missing[0].Destination})");
            }
        }
    }

    /// <summary>
    /// Canonical form of a vocabulary name: trimmed and lower-cased. Lower case is the
    /// wire contract for stored names (packs, seed data and node kinds all compare
    /// case-insensitively against it), so it is intentionally not round-trip casing.
    /// </summary>
    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Globalization",
        "CA1308:Normalize strings to uppercase",
        Justification = "Lower case is the stored vocabulary name format; changing it would break existing rows.")]
    public static string NormalizeName(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : NormalizeName(value);

    private static List<string> NormalizeNames(IReadOnlyList<string>? values)
    {
        if (values is null || values.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var normalized = new List<string>(values.Count);
        foreach (string value in values)
        {
            string name = NormalizeName(value);
            if (name.Length > 0 && seen.Add(name))
            {
                normalized.Add(name);
            }
        }

        return normalized;
    }
}
