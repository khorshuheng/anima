namespace Anima.Knowledge;

public static class KnowledgeKinds
{
    public const string Goal = "goal";
    public const string Task = "task";
    public const string Change = "change";
    public const string Decision = "decision";
    public const string Bug = "bug";
    public const string Test = "test";
    public const string Repo = "repo";
    public const string File = "file";
    public const string DatabaseTable = "db_table";
    public const string DatabaseView = "db_view";
    public const string Memory = "memory";
    public const string Note = "note";

    public static IReadOnlyList<NodeKindDefinition> Definitions { get; } =
    [
        new(1, Goal, "An outcome a plan advances."),
        new(2, Task, "A unit of work in a plan tree."),
        new(3, Change, "A delivered code or config change."),
        new(4, Decision, "A decision the work is justified by."),
        new(5, Bug, "A defect."),
        new(6, Test, "Evidence that an obligation holds."),
        new(7, Repo, "A project repository; the anchor for a work tree."),
        new(8, File, "A file touched or owned by work."),
        new(9, DatabaseTable, "A database table."),
        new(10, DatabaseView, "A database view."),
        new(11, Memory, "A durable long-term memory entry."),
        new(12, Note, "Free-form note."),
    ];
}

public static class KnowledgeEdgeTypes
{
    public const string PartOf = "part_of";
    public const string DependsOn = "depends_on";
    public const string Affects = "affects";
    public const string Implements = "implements";
    public const string Fixes = "fixes";
    public const string Modifies = "modifies";
    public const string JustifiedBy = "justified_by";
    public const string Contains = "contains";
    public const string BelongsTo = "belongs_to";
    public const string Verifies = "verifies";
    public const string Reveals = "reveals";
    public const string Mentions = "mentions";
    public const string RelatedTo = "related_to";
    public const string DerivedFrom = "derived_from";

    private static HashSet<string> Set(params string[] values)
        => new HashSet<string>(values, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<EdgeTypeDefinition> Definitions { get; } =
    [
        new(1, PartOf, "Decomposition: task to goal or task.", Set(KnowledgeKinds.Task), Set(KnowledgeKinds.Goal, KnowledgeKinds.Task)),
        new(2, DependsOn, "Ordering: the destination completes first.", Set(KnowledgeKinds.Task), Set(KnowledgeKinds.Task)),
        new(3, Affects, "Predicted blast radius.", Set(KnowledgeKinds.Task), Set(KnowledgeKinds.File, KnowledgeKinds.DatabaseTable, KnowledgeKinds.DatabaseView)),
        new(4, Implements, "A change delivers a task.", Set(KnowledgeKinds.Change), Set(KnowledgeKinds.Task), Resolves: true),
        new(5, Fixes, "A change resolves a defect.", Set(KnowledgeKinds.Change), Set(KnowledgeKinds.Bug), Resolves: true),
        new(6, Modifies, "Actual touched set.", Set(KnowledgeKinds.Change), Set(KnowledgeKinds.File, KnowledgeKinds.DatabaseTable, KnowledgeKinds.DatabaseView)),
        new(7, JustifiedBy, "The reasoning a change answers to.", Set(KnowledgeKinds.Change), Set(KnowledgeKinds.Decision)),
        new(8, Contains, "Physical containment.", Set(KnowledgeKinds.Repo), Set(KnowledgeKinds.File)),
        new(9, BelongsTo, "The project a goal advances.", Set(KnowledgeKinds.Goal), Set(KnowledgeKinds.Repo), SingleOutgoing: true),
        new(10, Verifies, "Evidence that an obligation holds.", Set(KnowledgeKinds.Test), Set(KnowledgeKinds.Goal, KnowledgeKinds.Task)),
        new(11, Reveals, "A test surfaced a defect.", Set(KnowledgeKinds.Test), Set(KnowledgeKinds.Bug)),
        new(12, Mentions, "A memory references a node.", Set(KnowledgeKinds.Memory, KnowledgeKinds.Note), Set()),
        new(13, RelatedTo, "Unconstrained association.", Set(), Set()),
        new(14, DerivedFrom, "A memory or note was derived from another node.", Set(KnowledgeKinds.Memory, KnowledgeKinds.Note), Set()),
    ];
}
