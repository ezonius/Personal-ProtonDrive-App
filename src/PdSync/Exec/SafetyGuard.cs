using PdSync.Config;
using PdSync.Plan;
using PdSync.Scan;
using PdSync.State;

namespace PdSync.Exec;

/// <summary>
/// Enforces the non-negotiable safety rules from SPEC §8 before execution.
/// </summary>
public static class SafetyGuard
{
    /// <summary>An empty local tree is a hard stop when the baseline knew content.</summary>
    public static void CheckLocalTree(LocalScan local, IReadOnlyList<EntryRecord> baseline)
    {
        if (local.Entries.Count == 0 && baseline.Any(e => !e.Frozen))
        {
            throw new SafetyException(
                $"local root '{local.Root}' is empty but state has {baseline.Count(e => !e.Frozen)} entries; " +
                "refusing to infer mass deletion (detach/empty-drive protection)");
        }
    }

    /// <summary>
    /// Deletion budget. A directory trash counts the number of baseline entries
    /// in that subtree, not just the directory row.
    /// </summary>
    public static void CheckDeletionBudget(
        PlanResult plan,
        IReadOnlyList<EntryRecord> baseline,
        SafetyOptions safety,
        bool force)
    {
        if (force || safety.IsUnlimited)
            return;

        var deleted = CountDeletedEntries(plan, baseline);
        if (deleted == 0)
            return;

        var total = baseline.Count(e => !e.Frozen);
        var percent = total == 0 ? 0d : deleted * 100d / total;
        var overCount = safety.MaxDeletes > 0 && deleted > safety.MaxDeletes;
        var overPercent = safety.MaxDeletePercent < 100 && percent > safety.MaxDeletePercent;

        if (!overCount && !overPercent)
            return;

        throw new SafetyException(
            $"deletion budget exceeded: {deleted} entr{(deleted == 1 ? "y" : "ies")} " +
            $"({percent:0.#}% of {total}); limits are maxDeletes={safety.MaxDeletes} and " +
            $"maxDeletePercent={safety.MaxDeletePercent}. Re-run with --force only after checking the tree.");
    }

    public static int CountDeletedEntries(PlanResult plan, IReadOnlyList<EntryRecord> baseline)
    {
        var roots = new List<string>();
        foreach (var action in plan.Actions.Where(a => a.IsDelete))
        {
            var key = action.PathKey;
            if (roots.Any(existing => key == existing || key.StartsWith(existing + "/", StringComparison.Ordinal)))
                continue;
            roots.Add(key);
        }

        var counted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var root in roots)
        {
            var prefix = root + "/";
            var matches = baseline
                .Where(e => e.PathKey == root || e.PathKey.StartsWith(prefix, StringComparison.Ordinal))
                .ToList();
            if (matches.Count == 0)
                counted.Add(root);
            else
                counted.UnionWith(matches.Select(e => e.PathKey));
        }

        return counted.Count;
    }
}
