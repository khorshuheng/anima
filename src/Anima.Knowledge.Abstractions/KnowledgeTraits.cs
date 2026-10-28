namespace Anima.Knowledge;

/// <summary>
/// Vocabulary traits. Generic graph logic keys off these instead of comparing
/// kind or edge type names, so a pack can add vocabulary that participates in
/// scoping and planning without a code change.
/// </summary>
public static class KnowledgeTraits
{
    /// <summary>
    /// Node kinds that take part in plan and scope reads. Core: <c>task</c>,
    /// <c>goal</c>.
    /// </summary>
    public const string Plannable = "plannable";

    /// <summary>
    /// Edge types that link a node to its parent for ancestor walks. Core:
    /// <c>part_of</c>.
    /// </summary>
    public const string Hierarchy = "hierarchy";

    /// <summary>
    /// Edge types that attach a scope to its anchor, terminating an ancestor walk.
    /// Core: <c>belongs_to</c>.
    /// </summary>
    public const string Anchor = "anchor";
}
