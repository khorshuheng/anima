using System.Reflection;

namespace Anima.Knowledge.Core.Tests;

/// <summary>
/// The embedded core pack is the source of truth for the standard vocabulary, while
/// <see cref="KnowledgeKinds"/> and <see cref="KnowledgeEdgeTypes"/> stay as
/// compile-time names for consumers such as the workflow store. These tests fail the
/// build when the two drift apart.
/// </summary>
public class CoreVocabularyTests
{
    private static readonly KnowledgeVocabularyCatalog Catalog = KnowledgeVocabularyLoader.LoadCore();

    [Fact]
    public void CorePackListsExactlyTheNodeKindConstants()
    {
        var constants = ConstantsOf(typeof(KnowledgeKinds));

        Assert.Equal(constants, Catalog.NodeKinds.Select(kind => kind.Name).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void CorePackListsExactlyTheEdgeTypeConstants()
    {
        var constants = ConstantsOf(typeof(KnowledgeEdgeTypes));

        Assert.Equal(constants, Catalog.EdgeTypes.Select(edge => edge.Name).ToHashSet(StringComparer.Ordinal));
    }

    [Fact]
    public void CorePackMatchesNodeKindDefinitions()
    {
        foreach (var definition in KnowledgeKinds.Definitions)
        {
            var kind = Assert.Single(Catalog.NodeKinds, candidate => candidate.Name == definition.Name);
            Assert.Equal(definition.Id, kind.Id);
            Assert.Equal(definition.Description, kind.Description);
            Assert.Equal(definition.Traits, kind.Traits);
        }

        Assert.Equal(KnowledgeKinds.Definitions.Count, Catalog.NodeKinds.Count);
    }

    [Fact]
    public void CorePackMatchesEdgeTypeDefinitions()
    {
        foreach (var definition in KnowledgeEdgeTypes.Definitions)
        {
            var edgeType = Assert.Single(Catalog.EdgeTypes, candidate => candidate.Name == definition.Name);
            Assert.Equal(definition.Id, edgeType.Id);
            Assert.Equal(definition.Description, edgeType.Description);
            Assert.Equal(definition.Resolves, edgeType.Resolves);
            Assert.Equal(definition.SingleOutgoing, edgeType.SingleOutgoing);
            Assert.True(definition.SourceKinds.SetEquals(edgeType.SourceKinds));
            Assert.True(definition.DestinationKinds.SetEquals(edgeType.DestinationKinds));
            Assert.Equal(definition.Traits, edgeType.Traits);
        }

        Assert.Equal(KnowledgeEdgeTypes.Definitions.Count, Catalog.EdgeTypes.Count);
    }

    [Fact]
    public void CorePackKeepsFixedIdsBelowThePackFloor()
    {
        Assert.All(Catalog.NodeKinds, kind => Assert.InRange(kind.Id, 1, KnowledgeVocabularyCatalog.PackIdFloor - 1));
        Assert.All(Catalog.EdgeTypes, edgeType => Assert.InRange(edgeType.Id, 1, KnowledgeVocabularyCatalog.PackIdFloor - 1));
        Assert.Equal(Catalog.NodeKinds.Count, Catalog.NodeKinds.Select(kind => kind.Id).Distinct().Count());
        Assert.Equal(Catalog.EdgeTypes.Count, Catalog.EdgeTypes.Select(edgeType => edgeType.Id).Distinct().Count());
    }

    [Fact]
    public void CorePackCarriesTheScopeTraits()
    {
        Assert.Equal(
            new[] { KnowledgeKinds.Goal, KnowledgeKinds.Task },
            Catalog.NodeKindsWithTrait(KnowledgeTraits.Plannable).Select(kind => kind.Name).ToArray());
        Assert.Equal(
            new[] { KnowledgeEdgeTypes.PartOf },
            Catalog.EdgeTypesWithTrait(KnowledgeTraits.Hierarchy).Select(edge => edge.Name).ToArray());
        Assert.Equal(
            new[] { KnowledgeEdgeTypes.BelongsTo },
            Catalog.EdgeTypesWithTrait(KnowledgeTraits.Anchor).Select(edge => edge.Name).ToArray());
    }

    [Fact]
    public void CorePackDerivesEndpointKindsFromPairs()
    {
        // Empty declared kinds mean "any", which normalizes to a single wildcard pair.
        var relatedTo = Catalog.RequireEdgeType(KnowledgeEdgeTypes.RelatedTo);
        Assert.Equal(new[] { new EdgeKindPair(KnowledgeVocabularyCatalog.Wildcard, KnowledgeVocabularyCatalog.Wildcard) }, relatedTo.Pairs.ToArray());
        Assert.Empty(relatedTo.SourceKinds);
        Assert.Empty(relatedTo.DestinationKinds);

        var mentions = Catalog.RequireEdgeType(KnowledgeEdgeTypes.Mentions);
        Assert.Empty(mentions.DestinationKinds);
        Assert.Contains(new EdgeKindPair(KnowledgeKinds.Memory, KnowledgeVocabularyCatalog.Wildcard), mentions.Pairs);

        var partOf = Catalog.RequireEdgeType(KnowledgeEdgeTypes.PartOf);
        Assert.Equal(2, partOf.Pairs.Count);
        Assert.True(Catalog.AllowsPair(KnowledgeEdgeTypes.PartOf, KnowledgeKinds.Task, KnowledgeKinds.Goal));
        Assert.True(Catalog.AllowsPair(KnowledgeEdgeTypes.PartOf, KnowledgeKinds.Task, KnowledgeKinds.Task));
        Assert.False(Catalog.AllowsPair(KnowledgeEdgeTypes.PartOf, KnowledgeKinds.Goal, KnowledgeKinds.Goal));
    }

    private static HashSet<string> ConstantsOf(Type type)
        => type.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field is { IsLiteral: true, IsInitOnly: false } && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);
}

public class VocabularyPackMergeTests
{
    private static readonly VocabularyPack Core = KnowledgeVocabularyLoader.LoadEmbeddedCore();

    [Fact]
    public void PackAddsNewKindsAndEdgeTypes()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:invoice", Description = "A billable unit.", RequiredProps = ["repo", "amount"] }],
            edges: [new EdgeTypeSpec { Name = "acme:billed_to", SourceKinds = ["acme:invoice"], DestinationKinds = ["repo"] }]);

        var catalog = KnowledgeVocabularyLoader.Build(Core, [pack]);

        var invoice = catalog.RequireKind("acme:invoice");
        Assert.Equal(new[] { "repo", "amount" }, invoice.RequiredProps.ToArray());
        Assert.True(invoice.Id >= KnowledgeVocabularyCatalog.PackIdFloor);
        Assert.True(catalog.AllowsPair("acme:billed_to", "acme:invoice", "repo"));
    }

    [Fact]
    public void PackMayNotRedefineCoreNodeKind()
    {
        var pack = Pack("acme", kinds: [new NodeKindSpec { Name = "task", Description = "Mine now." }]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("already defined", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackMayWidenCoreEdgePairs()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:invoice" }],
            edges: [new EdgeTypeSpec { Name = "related_to", Pairs = [new EdgeKindPair("acme:invoice", "repo")] }]);

        var catalog = KnowledgeVocabularyLoader.Build(Core, [pack]);

        Assert.True(catalog.AllowsPair(KnowledgeEdgeTypes.RelatedTo, "acme:invoice", "repo"));
        Assert.True(catalog.AllowsPair(KnowledgeEdgeTypes.RelatedTo, KnowledgeKinds.Goal, KnowledgeKinds.Task));
        Assert.Equal(KnowledgeEdgeTypes.Definitions.Single(edge => edge.Name == KnowledgeEdgeTypes.RelatedTo).Id, catalog.RequireEdgeType(KnowledgeEdgeTypes.RelatedTo).Id);
    }

    [Fact]
    public void PackMayNotChangeCoreOwnedFlags()
    {
        var pack = Pack("acme", edges: [new EdgeTypeSpec { Name = KnowledgeEdgeTypes.RelatedTo, Resolves = true }]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("may only widen", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackMayNotUseCoreIdRange()
    {
        var pack = Pack("acme", kinds: [new NodeKindSpec { Name = "acme:invoice", Id = 42 }]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("or above", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackIdsAreDeterministic()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:invoice" }, new NodeKindSpec { Name = "acme:shipment" }]);

        var first = KnowledgeVocabularyLoader.Build(Core, [pack]);
        var second = KnowledgeVocabularyLoader.Build(Core, [KnowledgeVocabularyLoader.Parse(
            """
            { "schema_version": 1, "name": "acme", "node_kinds": [{ "name": "acme:invoice" }, { "name": "acme:shipment" }] }
            """,
            "acme")]);

        Assert.Equal(first.RequireKind("acme:invoice").Id, second.RequireKind("acme:invoice").Id);
        Assert.Equal(first.RequireKind("acme:shipment").Id, second.RequireKind("acme:shipment").Id);
        Assert.NotEqual(first.RequireKind("acme:invoice").Id, first.RequireKind("acme:shipment").Id);
    }

    [Fact]
    public void SymmetricEdgeRequiresItselfAsInverse()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:person" }],
            edges: [new EdgeTypeSpec { Name = "acme:knows", Pairs = [new EdgeKindPair("acme:person", "acme:person")], Symmetric = true }]);

        var catalog = KnowledgeVocabularyLoader.Build(Core, [pack]);

        Assert.Equal("acme:knows", catalog.RequireEdgeType("acme:knows").InverseName);
    }

    [Fact]
    public void SymmetricEdgeRequiresSwappedPairs()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:person" }, new NodeKindSpec { Name = "acme:org" }],
            edges: [new EdgeTypeSpec { Name = "acme:knows", Pairs = [new EdgeKindPair("acme:person", "acme:org")], Symmetric = true }]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("mirrored pair", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Inverse_name_resolves_as_a_virtual_relation()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:invoice" }],
            edges:
            [
                new EdgeTypeSpec
                {
                    Name = "acme:billed_to",
                    Pairs = [new EdgeKindPair("acme:invoice", KnowledgeKinds.Repo)],
                    InverseName = "acme:billed_from",
                },
            ]);

        var catalog = KnowledgeVocabularyLoader.Build(Core, [pack]);

        Assert.False(catalog.TryGetEdgeType("acme:billed_from", out _));
        Assert.True(catalog.TryResolveEdgeType("acme:billed_from", out var resolution));
        Assert.True(resolution.Reversed);
        Assert.Equal("acme:billed_to", resolution.Definition.Name);
        Assert.Equal("acme:billed_from", catalog.RequireEdgeType("acme:billed_to").InverseName);
        Assert.Equal("acme:billed_to", catalog.TryGetCanonicalEdgeTypeName("acme:billed_from", out string? canonical) ? canonical : null);
    }

    [Fact]
    public void Inverse_name_must_not_be_a_defined_edge_type()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:invoice" }],
            edges:
            [
                new EdgeTypeSpec
                {
                    Name = "acme:billed_to",
                    Pairs = [new EdgeKindPair("acme:invoice", KnowledgeKinds.Repo)],
                    InverseName = "acme:billed_from",
                },
                new EdgeTypeSpec
                {
                    Name = "acme:billed_from",
                    Pairs = [new EdgeKindPair(KnowledgeKinds.Repo, "acme:invoice")],
                },
            ]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("define the relation once", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_edge_types_may_not_claim_the_same_inverse_name()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:invoice" }],
            edges:
            [
                new EdgeTypeSpec
                {
                    Name = "acme:billed_to",
                    Pairs = [new EdgeKindPair("acme:invoice", KnowledgeKinds.Repo)],
                    InverseName = "acme:billed_from",
                },
                new EdgeTypeSpec
                {
                    Name = "acme:charged_to",
                    Pairs = [new EdgeKindPair("acme:invoice", KnowledgeKinds.Repo)],
                    InverseName = "acme:billed_from",
                },
            ]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("both claim inverse_name", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Symmetric_edge_cannot_be_single_outgoing()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:person" }],
            edges:
            [
                new EdgeTypeSpec
                {
                    Name = "acme:knows",
                    Pairs = [new EdgeKindPair("acme:person", "acme:person")],
                    Symmetric = true,
                    SingleOutgoing = true,
                },
            ]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("cannot also be single_outgoing", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownKindReferenceIsRejected()
    {
        var pack = Pack("acme", edges: [new EdgeTypeSpec { Name = "acme:billed_to", Pairs = [new EdgeKindPair("acme:invoice", "repo")] }]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("undefined node kind 'acme:invoice'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PairsAndKindListsAreMutuallyExclusive()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:invoice" }],
            edges:
            [
                new EdgeTypeSpec
                {
                    Name = "acme:billed_to",
                    SourceKinds = ["acme:invoice"],
                    Pairs = [new EdgeKindPair("acme:invoice", "repo")],
                },
            ]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("not both", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void PackMayWidenACoreEdgeWithoutRestatingItsTraits()
    {
        var pack = Pack(
            "acme",
            kinds: [new NodeKindSpec { Name = "acme:invoice" }],
            edges: [new EdgeTypeSpec { Name = KnowledgeEdgeTypes.PartOf, SourceKinds = ["acme:invoice"], DestinationKinds = ["acme:invoice"] }]);

        var catalog = KnowledgeVocabularyLoader.Build(Core, [pack]);

        var partOf = catalog.RequireEdgeType(KnowledgeEdgeTypes.PartOf);
        Assert.Equal([KnowledgeTraits.Hierarchy], [.. partOf.Traits]);
        Assert.True(catalog.AllowsPair(KnowledgeEdgeTypes.PartOf, "acme:invoice", "acme:invoice"));
        Assert.Equal([KnowledgeEdgeTypes.PartOf], [.. catalog.EdgeTypesWithTrait(KnowledgeTraits.Hierarchy).Select(edge => edge.Name)]);
    }

    [Fact]
    public void PackMayNotChangeCoreTraits()
    {
        var pack = Pack(
            "acme",
            edges: [new EdgeTypeSpec { Name = KnowledgeEdgeTypes.PartOf, Traits = [KnowledgeTraits.Anchor] }]);

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("traits are fixed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownPackMemberIsRejected()
    {
        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Parse(
            """
            { "schema_version": 1, "name": "acme", "node_kinds": [{ "name": "acme:invoice", "sourc_kinds": [] }] }
            """,
            "acme"));

        Assert.Contains("sourc_kinds", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UnsupportedSchemaVersionIsRejected()
    {
        var pack = Pack("acme") with { SchemaVersion = 99 };

        var exception = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(Core, [pack]));

        Assert.Contains("schema_version", exception.Message, StringComparison.Ordinal);
    }

    private static VocabularyPack Pack(
        string name,
        IReadOnlyList<NodeKindSpec>? kinds = null,
        IReadOnlyList<EdgeTypeSpec>? edges = null)
        => new()
        {
            Name = name,
            NodeKinds = kinds ?? [],
            EdgeTypes = edges ?? [],
        };
}

/// <summary>Namespacing, warn mode and deprecation policy.</summary>
public sealed class VocabularyPolicyTests
{
    private static readonly VocabularyPack Core = KnowledgeVocabularyLoader.LoadEmbeddedCore();

    [Fact]
    public void Pack_added_names_must_be_namespaced()
    {
        var kind = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(
            Core,
            [Pack("acme", kinds: [new NodeKindSpec { Name = "invoice" }])]));
        Assert.Contains("must be namespaced as 'acme:<name>'", kind.Message, StringComparison.Ordinal);

        var edge = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(
            Core,
            [Pack("acme", kinds: [new NodeKindSpec { Name = "acme:invoice" }],
                edges: [new EdgeTypeSpec { Name = "billed_to", Pairs = [new EdgeKindPair("acme:invoice", KnowledgeKinds.Repo)] }])]));
        Assert.Contains("edge type 'billed_to' must be namespaced", edge.Message, StringComparison.Ordinal);

        var inverse = Assert.Throws<KnowledgeVocabularyException>(() => KnowledgeVocabularyLoader.Build(
            Core,
            [Pack("acme", kinds: [new NodeKindSpec { Name = "acme:invoice" }],
                edges: [new EdgeTypeSpec
                {
                    Name = "acme:billed_to",
                    Pairs = [new EdgeKindPair("acme:invoice", KnowledgeKinds.Repo)],
                    InverseName = "billed_from",
                }])]));
        Assert.Contains("inverse_name 'billed_from' must be namespaced", inverse.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Widening_a_core_edge_keeps_its_unprefixed_name()
    {
        var catalog = KnowledgeVocabularyLoader.Build(
            Core,
            [Pack("acme", kinds: [new NodeKindSpec { Name = "acme:invoice" }],
                edges: [new EdgeTypeSpec { Name = KnowledgeEdgeTypes.RelatedTo, Pairs = [new EdgeKindPair("acme:invoice", KnowledgeKinds.Repo)] }])]);

        Assert.True(catalog.AllowsPair(KnowledgeEdgeTypes.RelatedTo, "acme:invoice", KnowledgeKinds.Repo));
    }

    [Fact]
    public void Warn_mode_skips_the_bad_pack_and_keeps_the_rest()
    {
        string directory = Path.Combine(Path.GetTempPath(), "anima-warn-" + Guid.NewGuid().ToString("n"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(Path.Combine(directory, "bad.json"),
                """{ "schema_version": 1, "name": "bad", "node_kinds": [{ "name": "unprefixed" }] }""");
            File.WriteAllText(Path.Combine(directory, "good.json"),
                """{ "schema_version": 1, "name": "good", "node_kinds": [{ "name": "good:invoice" }] }""");
            var paths = KnowledgeVocabularyLoader.DiscoverPackFiles([directory], repoRoot: null, userConfigRoot: directory);

            var warnings = new List<string>();
            var catalog = KnowledgeVocabularyLoader.Load(paths, VocabularyMode.Warn, warnings.Add);

            Assert.Contains("good:invoice", catalog.NodeKinds.Select(kind => kind.Name));
            Assert.DoesNotContain("unprefixed", catalog.NodeKinds.Select(kind => kind.Name));
            Assert.Single(warnings);
            Assert.Contains("must be namespaced", warnings[0], StringComparison.Ordinal);

            var strict = Assert.Throws<KnowledgeVocabularyException>(
                () => KnowledgeVocabularyLoader.Load(paths, VocabularyMode.Strict));
            Assert.Contains("must be namespaced", strict.Message, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static VocabularyPack Pack(
        string name,
        IReadOnlyList<NodeKindSpec>? kinds = null,
        IReadOnlyList<EdgeTypeSpec>? edges = null)
        => new() { Name = name, NodeKinds = kinds ?? [], EdgeTypes = edges ?? [] };
}

/// <summary>
/// The validation surface used by `anima knowledge vocabulary validate` and
/// POST /api/vocabulary/validate. It validates on top of the loaded packs, so a pack
/// that widens another pack's entry behaves the same here and at startup.
/// </summary>
public sealed class VocabularyValidatorTests
{
    private static readonly VocabularyPack BasePack = new()
    {
        Name = "base",
        NodeKinds = [new NodeKindSpec { Name = "base:invoice" }],
    };

    private static readonly VocabularyPack WidenPack = new()
    {
        Name = "widen",
        EdgeTypes =
        [
            new EdgeTypeSpec
            {
                Name = KnowledgeEdgeTypes.RelatedTo,
                Pairs = [new EdgeKindPair("base:invoice", KnowledgeKinds.Repo)],
            },
        ],
    };

    private const string CandidateWideningToAnotherPackKind = """
        { "schema_version": 1, "name": "candidate",
          "edge_types": [{ "name": "related_to", "pairs": [["base:invoice", "note"]] }] }
        """;

    [Fact]
    public void Candidate_is_validated_against_the_loaded_packs()
    {
        var result = KnowledgeVocabularyValidator.Validate(
            CandidateWideningToAnotherPackKind,
            [BasePack, WidenPack]);

        Assert.True(result.Valid, result.Message);
        Assert.Equal("candidate", result.PackName);
        Assert.Equal(0, result.AddedNodeKinds);

        // Without the loaded packs the same candidate cannot resolve base:invoice.
        var alone = KnowledgeVocabularyValidator.Validate(CandidateWideningToAnotherPackKind, []);
        Assert.False(alone.Valid);
        Assert.Contains("undefined node kind 'base:invoice'", alone.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Invalid_documents_report_the_reason()
    {
        Assert.Equal(
            "candidate: pack document is empty",
            KnowledgeVocabularyValidator.Validate("null", []).Message);

        var unnamed = KnowledgeVocabularyValidator.Validate("{}", []);
        Assert.False(unnamed.Valid);
        Assert.Contains("pack name must not be empty", unnamed.Message!, StringComparison.Ordinal);

        var duplicate = KnowledgeVocabularyValidator.Validate(
            """{ "schema_version": 1, "name": "base" }""",
            [BasePack]);
        Assert.False(duplicate.Valid);
        Assert.Contains("loaded more than once", duplicate.Message!, StringComparison.Ordinal);

        var unnamespaced = KnowledgeVocabularyValidator.Validate(
            """{ "schema_version": 1, "name": "cand", "node_kinds": [{ "name": "invoice" }] }""",
            []);
        Assert.Contains("must be namespaced", unnamespaced.Message!, StringComparison.Ordinal);
    }

    [Fact]
    public void Counts_distinguish_owned_entries_from_inherited_ones()
    {
        var result = KnowledgeVocabularyValidator.Validate(
            """
            { "schema_version": 1, "name": "candidate",
              "node_kinds": [{ "name": "candidate:thing" }] }
            """,
            [BasePack]);

        Assert.True(result.Valid, result.Message);
        Assert.Equal(1, result.AddedNodeKinds);
        Assert.Equal(0, result.AddedEdgeTypes);
        Assert.Equal(23, result.NodeKinds);
    }
}
