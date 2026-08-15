namespace AnalyzeAot.CompilerHost;

internal sealed class RoslynHandleTable
{
    private const uint GenerationMask = 0x00ff_ffff;
    private const uint SlotMask = 0x00ff_ffff;

    private static int s_nextInteropId;

    private readonly object _gate = new();
    private readonly List<Entry> _entries = [];
    private readonly Stack<int> _freeSlots = [];
    private readonly ushort _interopId = GetNextInteropId();

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
        }

        disposable.Dispose();
    }

    private long Add(object value, bool isValue)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
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
            return Encode(slot, entry.Generation);
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
                "The Roslyn interop identity exhausted its handle slots.");
        }

        return ((long)_interopId << 48) |
            ((long)(generation & GenerationMask) << 24) |
            encodedSlot;
    }

    private void Decode(
        long handle,
        out int slot,
        out uint generation)
    {
        ushort interopId = (ushort)((ulong)handle >> 48);
        uint encodedSlot = (uint)((ulong)handle & SlotMask);
        generation = (uint)(((ulong)handle >> 24) & GenerationMask);
        if (interopId != _interopId ||
            encodedSlot == 0 ||
            generation == 0)
        {
            throw new ArgumentException(
                "The Roslyn handle is invalid or belongs to another interop identity.");
        }

        slot = checked((int)encodedSlot - 1);
    }

    private static uint NextGeneration(uint generation) =>
        generation == GenerationMask ? 1 : generation + 1;

    private static ushort GetNextInteropId()
    {
        while (true)
        {
            ushort interopId =
                (ushort)Interlocked.Increment(ref s_nextInteropId);
            if (interopId != 0)
            {
                return interopId;
            }
        }
    }

    private sealed class Entry
    {
        public object? Value { get; set; }

        public uint Generation { get; set; }

        public uint LastDisposedGeneration { get; set; }

        public bool IsValue { get; set; }
    }
}
