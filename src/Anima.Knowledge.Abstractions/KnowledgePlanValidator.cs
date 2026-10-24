namespace Anima.Knowledge;

/// <summary>Validates plan keys, parent/dependency references and cycles.</summary>
public static class KnowledgePlanValidator
{
    public static IReadOnlyDictionary<string, PlanTaskDraft> Validate(IReadOnlyList<PlanTaskDraft> tasks)
    {
        ArgumentNullException.ThrowIfNull(tasks);
        var byKey = new Dictionary<string, PlanTaskDraft>(StringComparer.Ordinal);
        foreach (var task in tasks)
        {
            if (string.IsNullOrWhiteSpace(task.Key))
            {
                throw new KnowledgeValidationException("every plan task requires a non-empty key");
            }

            if (!byKey.TryAdd(task.Key, task))
            {
                throw new KnowledgeValidationException($"duplicate plan task key '{task.Key}'");
            }
        }

        foreach (var task in tasks)
        {
            if (task.Parent is { } parent)
            {
                if (parent == task.Key)
                {
                    throw new KnowledgeValidationException($"task '{task.Key}' cannot be its own parent");
                }

                if (!byKey.ContainsKey(parent))
                {
                    throw new KnowledgeValidationException(
                        $"task '{task.Key}' references unknown parent key '{parent}'");
                }
            }

            foreach (var dependency in task.DependsOn ?? [])
            {
                if (dependency == task.Key)
                {
                    throw new KnowledgeValidationException($"task '{task.Key}' cannot depend on itself");
                }

                if (!byKey.ContainsKey(dependency))
                {
                    throw new KnowledgeValidationException(
                        $"task '{task.Key}' references unknown dependency key '{dependency}'");
                }
            }
        }

        var visiting = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        foreach (var key in byKey.Keys)
        {
            Visit(key);
        }

        return byKey;

        void Visit(string key)
        {
            if (visited.Contains(key))
            {
                return;
            }

            if (!visiting.Add(key))
            {
                throw new KnowledgeValidationException($"plan task wiring contains a cycle at '{key}'");
            }

            var task = byKey[key];
            if (task.Parent is { } parent)
            {
                Visit(parent);
            }

            foreach (var dependency in task.DependsOn ?? [])
            {
                Visit(dependency);
            }

            visiting.Remove(key);
            visited.Add(key);
        }
    }
}
