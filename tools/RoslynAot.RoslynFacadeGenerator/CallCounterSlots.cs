namespace RoslynAot.RoslynFacadeGenerator;

/// <summary>
/// The counter slot each instrumented boundary member records against, held
/// append-only in a checked-in file so that a slot, once assigned, never moves.
/// </summary>
/// <remarks>
/// Slots used to be a dense index over signatures sorted ordinally, computed
/// fresh each run. That is stable only while the member set is, and the member
/// set is exactly what a projection change moves: adding one member renumbered
/// every member sorting after it, so a change that touched 121 members rewrote
/// a <c>Record(N)</c> line in nearly every dispatcher in the tree — 23,655
/// lines whose only difference was the literal. The generator's contract is
/// that a clean regen reproduces the tree byte for byte precisely so the diff
/// is worth reading, and a five-figure mechanical churn on every projection
/// change defeats that.
///
/// Append-only is the same discipline migration Step 9 already specifies for
/// the contract itself. A retired member keeps its slot reserved rather than
/// freeing it for reuse: compacting would move every slot after the hole,
/// which is the churn this exists to prevent, and the cost of a hole is one
/// unread <c>long</c>.
///
/// The file is the source of truth and is read before the output directories
/// are recreated. Deleting it is not a neutral act — it reassigns every slot
/// and produces exactly the diff described above.
/// </remarks>
internal sealed class CallCounterSlots
{
    public const string FileName = "CallCounterSlots.txt";

    private const string Header =
        "# RoslynAot boundary call counter slots.\n" +
        "#\n" +
        "# Append-only: a slot is never moved and never reused, so that a\n" +
        "# projection change diffs as the members it adds rather than as a\n" +
        "# renumbering of every dispatcher in the tree. Entries with no\n" +
        "# corresponding projected member are retired slots, kept so the\n" +
        "# slots after them stay put.\n" +
        "#\n" +
        "# Generated. Edit the projection, not this file.\n";

    /// <summary>
    /// Control vtbl members share the slot space with projected members so
    /// that they are append-only too. They were previously placed at
    /// <c>memberCount + index</c>, which moved every one of them whenever the
    /// projection grew.
    /// </summary>
    private const string ControlPrefix = "control:";

    private readonly Dictionary<string, int> _slots;
    private int _nextSlot;

    private CallCounterSlots(Dictionary<string, int> slots)
    {
        _slots = slots;
        _nextSlot = slots.Count == 0 ? 0 : slots.Values.Max() + 1;
    }

    /// <summary>
    /// One past the highest slot ever assigned, including retired ones. This
    /// is the counter array length, not the number of live members.
    /// </summary>
    public int SlotCount => _nextSlot;

    public static string GetControlKey(string memberName) =>
        ControlPrefix + memberName;

    public static CallCounterSlots Load(string path)
    {
        var slots = new Dictionary<string, int>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return new CallCounterSlots(slots);
        }

        foreach (string line in File.ReadAllLines(path))
        {
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            string[] fields = line.Split('\t');
            if (fields.Length != 2 ||
                !int.TryParse(fields[0], out int slot))
            {
                throw new InvalidOperationException(
                    $"Malformed call counter slot line in '{path}': {line}");
            }

            if (!slots.TryAdd(fields[1], slot))
            {
                throw new InvalidOperationException(
                    $"Duplicate call counter slot key in '{path}': " +
                    fields[1]);
            }
        }

        return new CallCounterSlots(slots);
    }

    /// <summary>
    /// Reuses the slot a key already holds, or appends one. Keys are taken in
    /// the caller's order, so a run that introduces several members assigns
    /// them in a deterministic sequence.
    /// </summary>
    public void Reserve(IEnumerable<string> keys)
    {
        foreach (string key in keys)
        {
            if (!_slots.ContainsKey(key))
            {
                _slots.Add(key, _nextSlot++);
            }
        }
    }

    public int this[string key] => _slots[key];

    public void Save(string path)
    {
        IOrderedEnumerable<KeyValuePair<string, int>> ordered =
            _slots.OrderBy(entry => entry.Value);
        File.WriteAllText(
            path,
            Header +
            string.Concat(
                ordered.Select(entry => $"{entry.Value}\t{entry.Key}\n")));
    }
}
