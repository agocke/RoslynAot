namespace RoslynAot.Csc;

/// <summary>
/// One process-global table mapping compiler-owned Roslyn objects to handles.
/// There is exactly one instance for the whole compiler process — see
/// <see cref="RoslynInterop.Shared"/> — so a handle never needs to record
/// which table it came from; migration Step 4 retired that "control identity"
/// component once every analyzer in the process started sharing this table.
/// </summary>
internal sealed class RoslynHandleTable
{
    private const uint GenerationMask = 0x00ff_ffff;
    private const uint SlotMask = 0x00ff_ffff;

    private readonly Lock _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Stack<int> _freeSlots = [];

    // Reference types only: a compiler object that has already crossed keeps
    // the same handle on every later crossing, so analyzer-side reference
    // equality reflects Roslyn's own object identity (e.g. a shared
    // SyntaxTree handed to several analyzers). Context structs added via
    // AddValue are boxed per call and are never meaningfully "the same
    // object" twice, so they are not deduplicated. Strong keys are correct
    // here under the v1 "never release" policy: nothing in this table is
    // collected before the process exits, so a weak table would only add
    // overhead without freeing anything sooner.
    private readonly Dictionary<object, long> _reverseMap =
        new(ReferenceEqualityComparer.Instance);

    public long AddObject<T>(T value)
        where T : class =>
        Add(value, isValue: false);

    public long AddNullableObject<T>(T? value)
        where T : class =>
        value is null ? 0 : AddObject(value);

    public long AddValue<T>(T value)
        where T : struct =>
        Add(value, isValue: true);

    public T GetObject<T>(long handle)
        where T : class =>
        Get<T>(handle, isValue: false);

    public object GetObject(long handle) =>
        Get<object>(handle, isValue: false);

    public T GetValue<T>(long handle)
        where T : struct
    {
        if (handle == 0)
        {
            return default;
        }

        return Get<T>(handle, isValue: true);
    }

    public void DisposeObject<T>(long handle)
        where T : class, IDisposable
    {
        T? disposable = null;
        lock (_gate)
        {
            Decode(handle, out int slot, out uint generation);
            if ((uint)slot >= (uint)_entries.Count)
            {
                throw new ArgumentException("The Roslyn handle is invalid.");
            }

            Entry entry = _entries[slot];
            if (entry.LastDisposedGeneration == generation)
            {
                return;
            }

            if (entry.Value is null)
            {
                throw new ObjectDisposedException(
                    nameof(handle),
                    "The Roslyn handle has been released.");
            }

            if (entry.Generation != generation ||
                entry.IsValue ||
                entry.Value is not T typedValue)
            {
                throw new ArgumentException(
                    "The Roslyn handle has the wrong generation or type.");
            }

            disposable = typedValue;
            entry.Value = null;
            entry.LastDisposedGeneration = entry.Generation;
            entry.Generation = NextGeneration(entry.Generation);
            _freeSlots.Push(slot);
            _reverseMap.Remove(typedValue);
        }

        disposable.Dispose();
    }

    private long Add(object value, bool isValue)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            if (!isValue && _reverseMap.TryGetValue(value, out long existing))
            {
                return existing;
            }

            int slot;
            Entry entry;
            if (_freeSlots.TryPop(out slot))
            {
                entry = _entries[slot];
            }
            else
            {
                slot = _entries.Count;
                entry = new Entry { Generation = 1 };
                _entries.Add(entry);
            }

            entry.Value = value;
            entry.IsValue = isValue;
            long handle = Encode(slot, entry.Generation);
            if (!isValue)
            {
                _reverseMap.Add(value, handle);
            }

            return handle;
        }
    }

    private T Get<T>(long handle, bool isValue)
    {
        lock (_gate)
        {
            Decode(handle, out int slot, out uint generation);
            if ((uint)slot >= (uint)_entries.Count)
            {
                throw new ArgumentException("The Roslyn handle is invalid.");
            }

            Entry entry = _entries[slot];
            if (entry.Value is null)
            {
                throw new ObjectDisposedException(
                    nameof(handle),
                    "The Roslyn handle has been released.");
            }

            if (entry.Generation != generation ||
                entry.IsValue != isValue ||
                entry.Value is not T value)
            {
                throw new ArgumentException(
                    $"The Roslyn handle does not contain " +
                    $"'{typeof(T).FullName}'.");
            }

            return value;
        }
    }

    private long Encode(int slot, uint generation)
    {
        uint encodedSlot = checked((uint)(slot + 1));
        if (encodedSlot > SlotMask)
        {
            throw new InvalidOperationException(
                "The Roslyn handle table exhausted its handle slots.");
        }

        return ((long)(generation & GenerationMask) << 24) | encodedSlot;
    }

    private void Decode(
        long handle,
        out int slot,
        out uint generation)
    {
        uint encodedSlot = (uint)((ulong)handle & SlotMask);
        generation = (uint)(((ulong)handle >> 24) & GenerationMask);
        if (encodedSlot == 0 || generation == 0)
        {
            throw new ArgumentException("The Roslyn handle is invalid.");
        }

        slot = checked((int)encodedSlot - 1);
    }

    private static uint NextGeneration(uint generation) =>
        generation == GenerationMask ? 1 : generation + 1;

    private sealed class Entry
    {
        public object? Value { get; set; }

        public uint Generation { get; set; }

        public uint LastDisposedGeneration { get; set; }

        public bool IsValue { get; set; }
    }
}
