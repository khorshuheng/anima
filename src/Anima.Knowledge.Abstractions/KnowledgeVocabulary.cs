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
    public const string Spec = "spec";
    public const string Question = "question";
    public const string Answer = "answer";
    public const string Workflow = "workflow";
    public const string Phase = "phase";
    public const string Result = "result";
    public const string Finding = "finding";
    public const string Worktree = "worktree";
    public const string Agent = "agent";

    public static IReadOnlyList<NodeKindDefinition> Definitions { get; } =
    [
        new(1, Goal, "An outcome a plan advances.") { Traits = [KnowledgeTraits.Plannable] },
        new(2, Task, "A unit of work in a plan tree.") { Traits = [KnowledgeTraits.Plannable] },
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
        new(13, Spec, "A refined specification written by the analyst."),
        new(14, Question, "A clarification question for the user."),
        new(15, Answer, "An answer to a clarification question."),
        new(16, Workflow, "A harness-driven workflow state record."),
        new(17, Phase, "A workflow phase transition."),
        new(18, Result, "An implementation, merge, review or verification outcome."),
        new(19, Finding, "A defect or risk found during review."),
        new(20, Worktree, "A harness-owned git worktree assignment."),
        new(21, Agent, "An external agent identity."),
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
    public const string Tracks = "tracks";
    public const string Specifies = "specifies";
    public const string Asks = "asks";
    public const string Answers = "answers";
    public const string PhaseOf = "phase_of";
    public const string Precedes = "precedes";
    public const string Reviews = "reviews";
    public const string FoundIn = "found_in";
    public const string AssignedTo = "assigned_to";
    public const string WorktreeOf = "worktree_of";
    public const string ResultOf = "result_of";
    public const string ImplementsSpec = "implements_spec";
    public const string Supersedes = "supersedes";

    private static HashSet<string> Set(params string[] values)
        => new(values, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<EdgeTypeDefinition> Definitions { get; } =
    [
        new(1, PartOf, "Decomposition: task to goal or task.", Set(KnowledgeKinds.Task), Set(KnowledgeKinds.Goal, KnowledgeKinds.Task))
        {
            Acyclic = true,
            Traits = [KnowledgeTraits.Hierarchy],
        },
        new(2, DependsOn, "Ordering: the destination completes first.", Set(KnowledgeKinds.Task), Set(KnowledgeKinds.Task))
        {
            Acyclic = true,
        },
        new(3, Affects, "Predicted blast radius.", Set(KnowledgeKinds.Task), Set(KnowledgeKinds.File, KnowledgeKinds.DatabaseTable, KnowledgeKinds.DatabaseView)),
        new(4, Implements, "A change delivers a task.", Set(KnowledgeKinds.Change), Set(KnowledgeKinds.Task), Resolves: true),
        new(5, Fixes, "A change resolves a defect.", Set(KnowledgeKinds.Change), Set(KnowledgeKinds.Bug), Resolves: true),
        new(6, Modifies, "Actual touched set.", Set(KnowledgeKinds.Change), Set(KnowledgeKinds.File, KnowledgeKinds.DatabaseTable, KnowledgeKinds.DatabaseView)),
        new(7, JustifiedBy, "The reasoning a change answers to.", Set(KnowledgeKinds.Change), Set(KnowledgeKinds.Decision)),
        new(8, Contains, "Physical containment.", Set(KnowledgeKinds.Repo), Set(KnowledgeKinds.File)),
        new(9, BelongsTo, "The project a goal advances.", Set(KnowledgeKinds.Goal), Set(KnowledgeKinds.Repo), SingleOutgoing: true)
        {
            Traits = [KnowledgeTraits.Anchor],
        },
        new(10, Verifies, "Evidence that an obligation holds.", Set(KnowledgeKinds.Test), Set(KnowledgeKinds.Goal, KnowledgeKinds.Task)),
        new(11, Reveals, "A test surfaced a defect.", Set(KnowledgeKinds.Test), Set(KnowledgeKinds.Bug)),
        new(12, Mentions, "A memory references a node.", Set(KnowledgeKinds.Memory, KnowledgeKinds.Note), Set()),
        new(13, RelatedTo, "Unconstrained association.", Set(), Set()),
        new(14, DerivedFrom, "Derived from another node.", Set(), Set()),
        new(15, Tracks, "A workflow tracks its goal.", Set(KnowledgeKinds.Workflow), Set(KnowledgeKinds.Goal), SingleOutgoing: true),
        new(16, Specifies, "A spec describes a workflow or goal.", Set(KnowledgeKinds.Spec), Set(KnowledgeKinds.Workflow, KnowledgeKinds.Goal)),
        new(17, Asks, "A question belongs to a workflow.", Set(KnowledgeKinds.Question), Set(KnowledgeKinds.Workflow), SingleOutgoing: true),
        new(18, Answers, "An answer resolves a question.", Set(KnowledgeKinds.Answer), Set(KnowledgeKinds.Question), Resolves: true, SingleOutgoing: true),
        new(19, PhaseOf, "A phase belongs to a workflow.", Set(KnowledgeKinds.Phase), Set(KnowledgeKinds.Workflow), SingleOutgoing: true),
        new(20, Precedes, "Phase ordering.", Set(KnowledgeKinds.Phase), Set(KnowledgeKinds.Phase))
        {
            Acyclic = true,
        },
        new(21, Reviews, "A result reviews a workflow, spec or change.", Set(KnowledgeKinds.Result), Set(KnowledgeKinds.Workflow, KnowledgeKinds.Spec, KnowledgeKinds.Change)),
        new(22, FoundIn, "A finding was found in a result.", Set(KnowledgeKinds.Finding), Set(KnowledgeKinds.Result)),
        new(23, AssignedTo, "A worktree is assigned to an external agent.", Set(KnowledgeKinds.Worktree), Set(KnowledgeKinds.Agent)),
        new(24, WorktreeOf, "A worktree serves a workflow.", Set(KnowledgeKinds.Worktree), Set(KnowledgeKinds.Workflow), SingleOutgoing: true),
        new(25, ResultOf, "A result belongs to a workflow.", Set(KnowledgeKinds.Result), Set(KnowledgeKinds.Workflow), SingleOutgoing: true),
        new(26, ImplementsSpec, "A result implements a spec.", Set(KnowledgeKinds.Result), Set(KnowledgeKinds.Spec)),
        new(27, Supersedes, "A newer spec replaces an older one.", Set(KnowledgeKinds.Spec), Set(KnowledgeKinds.Spec), SingleOutgoing: true)
        {
            Acyclic = true,
        },
    ];
}
