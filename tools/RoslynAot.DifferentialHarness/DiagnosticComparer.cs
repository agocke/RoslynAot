namespace RoslynAot.DifferentialHarness;

internal enum DiffEntryKind
{
    Match,
    MissingDiagnostic,
    ExtraDiagnostic,
    SpanMismatch,
    SeverityMismatch,
    MessageMismatch,
}

internal sealed record DiffEntry(
    DiffEntryKind Kind,
    string RuleId,
    NormalizedDiagnostic? Managed,
    NormalizedDiagnostic? Native);

/// <summary>
/// Multiset comparison of two diagnostic lists, already filtered by the
/// caller to the rule IDs in scope. AD0001/AD0002 (analyzer crash)
/// results are handled separately by AnalyzerFailureParser, not here -
/// otherwise every analyzer crash would also register as a spurious
/// MissingDiagnostic.
/// </summary>
internal static class DiagnosticComparer
{
    public static IReadOnlyList<DiffEntry> Compare(
        IReadOnlyList<NormalizedDiagnostic> managed,
        IReadOnlyList<NormalizedDiagnostic> native)
    {
        var entries = new List<DiffEntry>();

        var managedByKey = ToMultimap(managed);
        var nativeByKey = ToMultimap(native);

        var allKeys = managedByKey.Keys
            .Concat(nativeByKey.Keys)
            .Distinct()
            .OrderBy(key => key, KeyComparer.Instance);

        var unpairedManaged = new List<NormalizedDiagnostic>();
        var unpairedNative = new List<NormalizedDiagnostic>();

        foreach (var key in allKeys)
        {
            Queue<NormalizedDiagnostic> managedQueue =
                managedByKey.TryGetValue(key, out var m)
                    ? new Queue<NormalizedDiagnostic>(m)
                    : new Queue<NormalizedDiagnostic>();
            Queue<NormalizedDiagnostic> nativeQueue =
                nativeByKey.TryGetValue(key, out var n)
                    ? new Queue<NormalizedDiagnostic>(n)
                    : new Queue<NormalizedDiagnostic>();

            while (managedQueue.Count > 0 && nativeQueue.Count > 0)
            {
                NormalizedDiagnostic managedEntry = managedQueue.Dequeue();
                NormalizedDiagnostic nativeEntry = nativeQueue.Dequeue();
                entries.Add(ComparePair(managedEntry, nativeEntry));
            }

            unpairedManaged.AddRange(managedQueue);
            unpairedNative.AddRange(nativeQueue);
        }

        ReclassifySpanMismatches(unpairedManaged, unpairedNative, entries);

        foreach (NormalizedDiagnostic entry in unpairedManaged
            .OrderBy(d => d, DiagnosticOrder.Instance))
        {
            entries.Add(
                new DiffEntry(
                    DiffEntryKind.MissingDiagnostic,
                    entry.RuleId,
                    entry,
                    null));
        }

        foreach (NormalizedDiagnostic entry in unpairedNative
            .OrderBy(d => d, DiagnosticOrder.Instance))
        {
            entries.Add(
                new DiffEntry(
                    DiffEntryKind.ExtraDiagnostic,
                    entry.RuleId,
                    null,
                    entry));
        }

        return entries;
    }

    private static DiffEntry ComparePair(
        NormalizedDiagnostic managed,
        NormalizedDiagnostic native)
    {
        if (!string.Equals(managed.Level, native.Level, StringComparison.Ordinal))
        {
            return new DiffEntry(
                DiffEntryKind.SeverityMismatch, managed.RuleId, managed, native);
        }

        if (!string.Equals(
            managed.Message, native.Message, StringComparison.Ordinal))
        {
            return new DiffEntry(
                DiffEntryKind.MessageMismatch, managed.RuleId, managed, native);
        }

        return new DiffEntry(DiffEntryKind.Match, managed.RuleId, managed, native);
    }

    /// <summary>
    /// An id-set (or even id+message) comparison alone would miss a
    /// diagnostic that moved to the wrong span - that is exactly the
    /// blind spot problem 15's CA1200 example fell into. Re-pair
    /// same-(rule,file) leftovers as an explicit SpanMismatch instead of
    /// letting them silently become one missing and one extra entry.
    /// </summary>
    private static void ReclassifySpanMismatches(
        List<NormalizedDiagnostic> unpairedManaged,
        List<NormalizedDiagnostic> unpairedNative,
        List<DiffEntry> entries)
    {
        var nativeByRuleFile = unpairedNative
            .Select((diagnostic, index) => (diagnostic, index))
            .ToLookup(pair => (pair.diagnostic.RuleId, pair.diagnostic.File));

        var consumedNativeIndexes = new HashSet<int>();
        var consumedManagedIndexes = new HashSet<int>();

        for (int managedIndex = 0; managedIndex < unpairedManaged.Count; managedIndex++)
        {
            NormalizedDiagnostic managedEntry = unpairedManaged[managedIndex];
            var candidate = nativeByRuleFile[(managedEntry.RuleId, managedEntry.File)]
                .FirstOrDefault(pair => !consumedNativeIndexes.Contains(pair.index));
            if (candidate == default)
            {
                continue;
            }

            consumedManagedIndexes.Add(managedIndex);
            consumedNativeIndexes.Add(candidate.index);
            entries.Add(
                new DiffEntry(
                    DiffEntryKind.SpanMismatch,
                    managedEntry.RuleId,
                    managedEntry,
                    candidate.diagnostic));
        }

        RemoveConsumed(unpairedManaged, consumedManagedIndexes);
        RemoveConsumed(unpairedNative, consumedNativeIndexes);
    }

    private static void RemoveConsumed(
        List<NormalizedDiagnostic> items,
        HashSet<int> consumedIndexes)
    {
        for (int index = items.Count - 1; index >= 0; index--)
        {
            if (consumedIndexes.Contains(index))
            {
                items.RemoveAt(index);
            }
        }
    }

    private static Dictionary<
        (string RuleId, string File, int StartLine, int StartColumn,
            int EndLine, int? EndColumn),
        List<NormalizedDiagnostic>> ToMultimap(
        IReadOnlyList<NormalizedDiagnostic> diagnostics)
    {
        var map = new Dictionary<
            (string, string, int, int, int, int?),
            List<NormalizedDiagnostic>>();
        foreach (NormalizedDiagnostic diagnostic in diagnostics)
        {
            if (!map.TryGetValue(diagnostic.ComparisonKey, out List<NormalizedDiagnostic>? list))
            {
                list = [];
                map[diagnostic.ComparisonKey] = list;
            }

            list.Add(diagnostic);
        }

        return map;
    }

    private sealed class KeyComparer
        : IComparer<(string RuleId, string File, int StartLine, int StartColumn,
            int EndLine, int? EndColumn)>
    {
        public static readonly KeyComparer Instance = new();

        public int Compare(
            (string RuleId, string File, int StartLine, int StartColumn,
                int EndLine, int? EndColumn) x,
            (string RuleId, string File, int StartLine, int StartColumn,
                int EndLine, int? EndColumn) y)
        {
            int ruleCompare = string.CompareOrdinal(x.RuleId, y.RuleId);
            if (ruleCompare != 0)
            {
                return ruleCompare;
            }

            int fileCompare = string.CompareOrdinal(x.File, y.File);
            if (fileCompare != 0)
            {
                return fileCompare;
            }

            if (x.StartLine != y.StartLine)
            {
                return x.StartLine.CompareTo(y.StartLine);
            }

            return x.StartColumn.CompareTo(y.StartColumn);
        }
    }

    private sealed class DiagnosticOrder : IComparer<NormalizedDiagnostic>
    {
        public static readonly DiagnosticOrder Instance = new();

        public int Compare(NormalizedDiagnostic? x, NormalizedDiagnostic? y)
        {
            if (x is null || y is null)
            {
                return 0;
            }

            return KeyComparer.Instance.Compare(x.ComparisonKey, y.ComparisonKey);
        }
    }
}
