using Microsoft.CodeAnalysis;

namespace RoslynAot.RoslynFacadeGenerator;

/// <summary>
/// How a type the projection does not own can cross the boundary.
/// </summary>
/// <remarks>
/// The facade generator can substitute its own <c>Microsoft.CodeAnalysis.ISymbol</c>
/// for Roslyn's, because it owns that name in the analyzer's closure. It can
/// never substitute its own <c>System.Collections.Immutable.ImmutableArray&lt;T&gt;</c>:
/// the analyzer binds to the framework's copy, and the framework's copy is a
/// struct with a <c>T[]</c> field and no seam to put a handle behind.
///
/// So every type in a projected signature that is not from a facade assembly is
/// a place where the boundary cannot insert itself, and the question "what does
/// this type need in order to arrive intact" has to be answered for it
/// specifically. This enum is the set of answers.
/// </remarks>
internal enum ForeignTransport
{
    /// <summary>
    /// Bit-identical on both sides, so copying is the identity function.
    /// Primitives, enums over primitives, <c>string</c>, <c>void</c>.
    /// </summary>
    Primitive,

    /// <summary>
    /// The analyzer side can supply the behaviour — an interface it implements,
    /// or a class whose contract is overridable. The instance never moves and
    /// every operation crosses, so comparers, complexity, and laziness stay
    /// with the object that has them.
    /// </summary>
    Proxy,

    /// <summary>
    /// The analyzer side cannot supply behaviour, so a real instance has to be
    /// rebuilt from its contents. Faithful only where the type's observable
    /// behaviour is a function of those contents — which is a claim about the
    /// specific type, not something the shape can tell you.
    /// </summary>
    Clone,

    /// <summary>
    /// A delegate. It closes over analyzer state that has no compiler-side
    /// representation, so it crosses as a registered <c>(fn, ctx)</c> pair
    /// rather than as a value.
    /// </summary>
    Callback,

    /// <summary>
    /// Nothing either side can hand over would be accepted by the other.
    /// Members using it stay unsupported until the entry says otherwise.
    /// </summary>
    Unrepresentable,
}

internal sealed record ForeignTypeEntry(
    ForeignTransport Transport,
    string Reason);

/// <summary>
/// One foreign type as it appears across the whole projection, with the
/// counts that say whether its classification is load-bearing today or a note
/// about work not yet started.
/// </summary>
internal sealed record ForeignTypeUse(
    string CanonicalId,
    string DisplayName,
    ForeignTypeEntry Entry,
    bool Declared,
    int Uses,
    int SupportedUses,
    string? FirstSupportedCallId);

/// <summary>
/// The transport class of every type in a projected signature that the
/// projection does not own, declared where it matters and derived by a named
/// rule otherwise.
/// </summary>
/// <remarks>
/// This exists because the copy is the part of the boundary that fails
/// quietly. A missing member throws; a wrong-shaped handle throws; a collection
/// copied without its comparer answers <c>Contains</c> with the wrong result
/// and reports a wrong diagnostic. The only defence is to enumerate the types
/// where copying is forced and require an argument for each one, so that a
/// Roslyn upgrade adding a framework type to the analyzer surface fails the
/// build rather than silently acquiring a default.
/// </remarks>
internal static class ProjectionForeignTypes
{
    private static readonly IReadOnlyDictionary<string, ForeignTypeEntry>
        s_entries = new Dictionary<string, ForeignTypeEntry>(
            StringComparer.Ordinal)
    {
        // ---- Cloned, and faithful ------------------------------------------

        ["[System.Collections.Immutable]T:System.Collections.Immutable.ImmutableArray`1"] = new(
            ForeignTransport.Clone,
            "A struct over a T[], so there is no seam to put a handle behind. " +
            "Copying is faithful because it stores no comparer and its " +
            "Contains and IndexOf are EqualityComparer<T>.Default, which is " +
            "exactly what the copied array answers with."),

        ["[System.Runtime]T:System.Nullable`1"] = new(
            ForeignTransport.Clone,
            "Two fields, one of them the value. It clones exactly when its " +
            "type argument does, so it adds no decision of its own."),

        ["[System.Runtime]T:System.Collections.Generic.KeyValuePair`2"] = new(
            ForeignTransport.Clone,
            "An immutable pair of fields with no behaviour to lose."),

        ["[System.Runtime]T:System.ValueTuple`2"] = new(
            ForeignTransport.Clone,
            "An immutable pair of fields with no behaviour to lose."),

        ["[System.Runtime]T:System.ArraySegment`1"] = new(
            ForeignTransport.Clone,
            "An array reference plus an offset and a count; the segment is " +
            "rebuilt over the cloned array."),

        ["[System.Runtime]T:System.TimeSpan"] = new(
            ForeignTransport.Clone,
            "A tick count."),

        ["[System.Runtime]T:System.Guid"] = new(
            ForeignTransport.Clone,
            "Sixteen bytes with no identity beyond their value."),

        ["[System.Runtime]T:System.Version"] = new(
            ForeignTransport.Clone,
            "Four integers, immutable, compared by value."),

        ["[System.Security.Cryptography]T:System.Security.Cryptography.HashAlgorithmName"] = new(
            ForeignTransport.Clone,
            "A wrapper over a name string."),

        ["[System.Collections]T:System.Collections.Generic.List`1"] = new(
            ForeignTransport.Clone,
            "Reached only through DirectiveTriviaSyntax.GetRelatedDirectives, " +
            "which builds a fresh list per call and hands it away. Nothing " +
            "observes its identity and it holds no comparer, so a copy is the " +
            "same list."),

        ["[System.Runtime]T:System.Uri"] = new(
            ForeignTransport.Clone,
            "Rebuilt from its absolute string form, which round-trips."),

        ["[System.Runtime]T:System.Object"] = new(
            ForeignTransport.Clone,
            "In return position Roslyn uses it for a boxed C# constant, which " +
            "clones under a type tag naming which primitive it is. In " +
            "parameter position it is the object-typed Equals overload, where " +
            "the argument is a proxy and no clone happens. A runtime object " +
            "that is neither is a degradation the transport must report rather " +
            "than guess at."),

        // ---- Cloned, and only faithful with their behaviour carried --------

        // The keyed collections are classified by what can be built today, not
        // by what could be built. Both are forced clones — sealed, or with no
        // virtual members to override — and a copy of either silently
        // substitutes the default comparer for the instance's own, which is
        // problem 22 exactly. Calling that Clone would record the intended
        // design while leaving the build willing to accept the broken one.
        // Unrepresentable fails closed instead: the moment a member returning
        // one is marked supported, validation stops the build and names it.
        //
        // The path to Clone is short and worth keeping written down: copy the
        // pairs and rebuild with CreateRange(keyComparer, valueComparer,
        // pairs), with the comparers crossing as Proxy because
        // IEqualityComparer<T> is an interface. Flip these two entries when
        // that transport exists.
        ["[System.Collections.Immutable]T:System.Collections.Immutable.ImmutableDictionary`2"] = new(
            ForeignTransport.Unrepresentable,
            "Sealed, so the analyzer side cannot supply behaviour and the " +
            "pairs would have to be copied — but the instance carries a " +
            "KeyComparer and a ValueComparer, and Roslyn's analyzer config " +
            "keys compare case-insensitively, so a copy answers lookups " +
            "wrongly with nothing to notice. Cannot cross until the comparer " +
            "crosses with it."),

        ["[System.Collections]T:System.Collections.Generic.Dictionary`2"] = new(
            ForeignTransport.Unrepresentable,
            "Its members are not virtual, so subclassing supplies no " +
            "behaviour and the entries would have to be copied. Same defect " +
            "as ImmutableDictionary: the instance's IEqualityComparer<TKey> " +
            "is part of what is being projected and a copy discards it."),

        ["[System.Runtime]T:System.Exception"] = new(
            ForeignTransport.Clone,
            "Type and message clone; the stack trace, the inner chain, and " +
            "the identity do not. Acceptable only because it appears where an " +
            "analyzer reports a failure rather than inspects one."),

        ["[System.Runtime]T:System.Globalization.CultureInfo"] = new(
            ForeignTransport.Clone,
            "Rebuilt by name. Custom cultures and per-instance format " +
            "overrides do not survive, which is a real loss the member using " +
            "it has to be willing to take."),

        // ---- Proxied: the analyzer side supplies the behaviour -------------

        ["[System.Runtime]T:System.Collections.Generic.IEnumerable`1"] = new(
            ForeignTransport.Proxy,
            "An interface, so an analyzer-side implementation over a handle " +
            "is indistinguishable. Proxying also keeps laziness, which " +
            "matters because Roslyn's are iterator sequences over trees."),

        ["[System.Runtime]T:System.Collections.Generic.IEnumerator`1"] = new(
            ForeignTransport.Proxy,
            "An interface with live position; a copy would have to snapshot " +
            "the sequence to have anything to be positioned in."),

        ["[System.Runtime]T:System.Collections.IEnumerator"] = new(
            ForeignTransport.Proxy,
            "An interface with live position; a copy would have to snapshot " +
            "the sequence to have anything to be positioned in."),

        ["[System.Runtime]T:System.Collections.Generic.ICollection`1"] = new(
            ForeignTransport.Proxy,
            "The declared type promises membership, so the comparer is part " +
            "of the contract and only the owning collection can answer it."),

        ["[System.Runtime]T:System.Collections.Generic.IList`1"] = new(
            ForeignTransport.Proxy,
            "The declared type promises membership and indexing, so the " +
            "comparer is part of the contract and only the owner can answer."),

        ["[System.Runtime]T:System.Collections.Generic.IReadOnlyList`1"] = new(
            ForeignTransport.Proxy,
            "An interface; proxying keeps indexing on the owner rather than " +
            "forcing a snapshot to index into."),

        ["[System.Runtime]T:System.Collections.Generic.IReadOnlyDictionary`2"] = new(
            ForeignTransport.Proxy,
            "The declared type promises keyed lookup, so the key comparer is " +
            "part of the contract and only the owning dictionary has it."),

        ["[System.Runtime]T:System.Collections.Generic.IEqualityComparer`1"] = new(
            ForeignTransport.Proxy,
            "A comparer is behaviour and nothing else, so it is the one thing " +
            "that must never be copied. This is also what lets the sealed " +
            "dictionaries above be rebuilt faithfully."),

        ["[System.Runtime]T:System.StringComparer"] = new(
            ForeignTransport.Proxy,
            "Abstract with an overridable Compare, Equals, and GetHashCode, " +
            "so an analyzer-side subclass forwarding to the compiler's " +
            "instance is faithful. Reached through " +
            "AnalyzerConfigOptions.KeyComparer and " +
            "CaseInsensitiveComparison.Comparer, whose whole point is that " +
            "they are not the ordinal default."),

        ["[System.Runtime]T:System.IFormatProvider"] = new(
            ForeignTransport.Proxy,
            "An interface, and the analyzer supplies it as often as it " +
            "receives one: LocalizableString.GetText takes the analyzer's."),

        ["[System.Runtime]T:System.IO.Stream"] = new(
            ForeignTransport.Proxy,
            "Abstract with overridable Read, Write, Seek, and Position, so a " +
            "forwarding subclass is faithful. A copy could not be: the " +
            "position is live state the other side keeps mutating."),

        ["[System.Runtime]T:System.IO.TextWriter"] = new(
            ForeignTransport.Proxy,
            "Abstract, and a sink: the point is that writes reach the other " +
            "side's destination, which a copy cannot do."),

        ["[System.Runtime]T:System.IO.TextReader"] = new(
            ForeignTransport.Proxy,
            "Abstract, and a source with live position."),

        ["[System.Runtime]T:System.Text.Encoding"] = new(
            ForeignTransport.Proxy,
            "Abstract with overridable GetBytes and GetChars. Reconstructing " +
            "by code page would work for the built-in encodings and silently " +
            "substitute the wrong one for a custom subclass, so it is " +
            "forwarded instead."),

        ["[System.Runtime]T:System.Text.StringBuilder"] = new(
            ForeignTransport.Proxy,
            "Sealed, but it appears only as a parameter the callee appends " +
            "to, so what has to cross is the appending, not the buffer. The " +
            "compiler side writes into a forwarding wrapper over the " +
            "analyzer's instance."),

        // ---- Callbacks -----------------------------------------------------

        ["[System.Runtime]T:System.Action"] = new(
            ForeignTransport.Callback,
            "A delegate over analyzer state; it crosses as a registration."),

        ["[System.Runtime]T:System.Action`1"] = new(
            ForeignTransport.Callback,
            "A delegate over analyzer state; it crosses as a registration. " +
            "This is the shape every analyzer action registration takes."),

        ["[System.Runtime]T:System.Action`2"] = new(
            ForeignTransport.Callback,
            "A delegate over analyzer state; it crosses as a registration."),

        ["[System.Runtime]T:System.Action`3"] = new(
            ForeignTransport.Callback,
            "A delegate over analyzer state; it crosses as a registration."),

        ["[System.Runtime]T:System.Func`1"] = new(
            ForeignTransport.Callback,
            "A delegate over analyzer state; it crosses as a registration."),

        ["[System.Runtime]T:System.Func`2"] = new(
            ForeignTransport.Callback,
            "A delegate over analyzer state; it crosses as a registration."),

        ["[System.Runtime]T:System.Func`3"] = new(
            ForeignTransport.Callback,
            "A delegate over analyzer state; it crosses as a registration."),

        ["[System.Runtime]T:System.EventHandler`1"] = new(
            ForeignTransport.Callback,
            "A delegate over analyzer state; it crosses as a registration."),

        ["[System.Runtime]T:System.AsyncCallback"] = new(
            ForeignTransport.Callback,
            "A delegate over analyzer state, reached only through the " +
            "Begin/End pattern on Roslyn's own delegate types."),

        ["[System.Runtime]T:System.Threading.CancellationToken"] = new(
            ForeignTransport.Clone,
            "Not a clone of the value - a clone of the one edge the value " +
            "carries. The struct is a single field over a " +
            "CancellationTokenSource, every member reads that source's " +
            "private state directly, and nothing on the source is virtual, so " +
            "the receiver can only hold a real source of its own. The sender " +
            "registers on its token and the receiver cancels its source when " +
            "the edge arrives. Faithful for the observable surface because " +
            "cancellation is monotonic and Cancel is idempotent, so a late " +
            "edge is late and never wrong, and because registering on an " +
            "already-cancelled token fires synchronously, so the " +
            "already-cancelled case needs no separate state read. Handle 0 is " +
            "default, which keeps CanBeCanceled false rather than minting a " +
            "source that reports true. Declared degradation: the edge is " +
            "one-way per crossing, so a token the receiver cancels does not " +
            "propagate back to the sender."),

        // ---- Unrepresentable ------------------------------------------------

        ["[System.Runtime]T:System.Threading.Tasks.Task`1"] = new(
            ForeignTransport.Unrepresentable,
            "Bound to a scheduler and a continuation chain in the heap that " +
            "created it. Neither side can hand the other something the other " +
            "would await correctly."),

        ["[System.Runtime]T:System.ReadOnlySpan`1"] = new(
            ForeignTransport.Unrepresentable,
            "A ref struct: it cannot be stored, boxed, or outlive the call, " +
            "so there is nothing for a handle to refer to."),

        ["[System.Runtime]T:System.IntPtr"] = new(
            ForeignTransport.Unrepresentable,
            "Raw pointers into an image the compiler mapped, plus the method " +
            "pointer half of a delegate's constructor. Both mean something " +
            "only in the module that produced them."),

        ["[System.Runtime]T:System.Type"] = new(
            ForeignTransport.Unrepresentable,
            "Reflection identity is per-module under NativeAOT, so a Type " +
            "from the compiler names nothing in the analyzer's closure. " +
            "Problem 20 puts reflection over Roslyn types outside the " +
            "supported set for the same reason."),

        ["[System.Runtime]T:System.Reflection.Assembly"] = new(
            ForeignTransport.Unrepresentable,
            "Reflection identity is per-module; see System.Type."),

        ["[System.Runtime]T:System.Resources.ResourceManager"] = new(
            ForeignTransport.Unrepresentable,
            "Resolves resources out of the assembly that owns it. The " +
            "analyzer's resources are in the analyzer module, which is why " +
            "LocalizableResourceString is declared analyzer-local rather than " +
            "given a transport."),

        ["[System.Runtime]T:System.IAsyncResult"] = new(
            ForeignTransport.Unrepresentable,
            "Bound to a live asynchronous operation. Reached only through the " +
            "Begin/End members Roslyn's delegate types inherit, which no " +
            "analyzer calls."),

        ["[System.Reflection.Metadata]T:System.Reflection.Metadata.MetadataReader"] = new(
            ForeignTransport.Unrepresentable,
            "A reader over a mapped PE image; its handles are indices into " +
            "that specific reader."),

        ["[System.Reflection.Metadata]T:System.Reflection.Metadata.MethodDefinitionHandle"] = new(
            ForeignTransport.Unrepresentable,
            "An index into a MetadataReader, meaningless without the reader " +
            "it indexes."),

        ["[System.Reflection.Metadata]T:System.Reflection.Metadata.StandaloneSignatureHandle"] = new(
            ForeignTransport.Unrepresentable,
            "An index into a MetadataReader, meaningless without the reader " +
            "it indexes."),

        ["[System.Reflection.Metadata]T:System.Reflection.Metadata.TypeDefinitionHandle"] = new(
            ForeignTransport.Unrepresentable,
            "An index into a MetadataReader, meaningless without the reader " +
            "it indexes."),
    };

    public static IEnumerable<string> Ids => s_entries.Keys;

    public static bool TryGet(
        string canonicalId,
        out ForeignTypeEntry entry) =>
        s_entries.TryGetValue(canonicalId, out entry!);

    /// <summary>
    /// The transport class of a foreign type, and whether it was declared
    /// above or derived. Only <see cref="ForeignTransport.Primitive"/> is
    /// derivable without a claim about the specific type, which is why it is
    /// the only class <see cref="ProjectionValidation"/> accepts underived.
    /// </summary>
    public static (ForeignTypeEntry Entry, bool Declared) Get(
        ITypeSymbol type,
        string canonicalId)
    {
        if (TryGet(canonicalId, out ForeignTypeEntry declared))
        {
            return (declared, true);
        }

        if (IsPrimitive(type))
        {
            return (
                new ForeignTypeEntry(
                    ForeignTransport.Primitive,
                    "Derived: bit-identical on both sides, so copying it is " +
                    "the identity function."),
                false);
        }

        if (type.TypeKind == TypeKind.Delegate)
        {
            return (
                new ForeignTypeEntry(
                    ForeignTransport.Callback,
                    "Derived: a delegate closes over state in the heap that " +
                    "created it."),
                false);
        }

        if (type.IsRefLikeType || type is IPointerTypeSymbol)
        {
            return (
                new ForeignTypeEntry(
                    ForeignTransport.Unrepresentable,
                    "Derived: cannot be stored or outlive the call, so there " +
                    "is nothing for a handle to refer to."),
                false);
        }

        // Everything past here is a claim about the type, not about its shape:
        // whether a copy of it behaves like the original. Derivation stops
        // rather than guess, and validation requires a declaration.
        return (
            new ForeignTypeEntry(
                ForeignTransport.Clone,
                "Derived: the analyzer binds to the framework's copy of this " +
                "type, so no proxy can be substituted for it and an instance " +
                "must be rebuilt. Whether that rebuild is faithful is a claim " +
                "about this type that no rule can make."),
            false);
    }

    /// <summary>
    /// Every type in a projected signature that no facade assembly owns,
    /// keyed by its original definition so that
    /// <c>ImmutableArray&lt;ISymbol&gt;</c> and
    /// <c>ImmutableArray&lt;Location&gt;</c> are one decision rather than two.
    /// </summary>
    public static IReadOnlyList<ForeignTypeUse> Collect(ProjectionModel model)
    {
        var facadeAssemblies = model.Assemblies
            .Select(assembly => assembly.Name)
            .ToHashSet(StringComparer.Ordinal);
        var uses = new Dictionary<string, int>(StringComparer.Ordinal);
        var supported = new Dictionary<string, int>(StringComparer.Ordinal);
        var firstSupported = new Dictionary<string, string>(
            StringComparer.Ordinal);
        var symbols = new Dictionary<string, ITypeSymbol>(
            StringComparer.Ordinal);

        foreach (ProjectedCall call in model.Calls)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (ITypeSymbol type in GetSignatureTypes(call))
            {
                foreach (ITypeSymbol foreign in Expand(type, facadeAssemblies))
                {
                    string id = CanonicalSignatureBuilder.GetCanonicalId(
                        foreign.OriginalDefinition);
                    symbols[id] = foreign.OriginalDefinition;
                    if (!seen.Add(id))
                    {
                        continue;
                    }

                    uses[id] = uses.GetValueOrDefault(id) + 1;
                    if (!call.IsSupported)
                    {
                        continue;
                    }

                    supported[id] = supported.GetValueOrDefault(id) + 1;
                    if (!firstSupported.ContainsKey(id))
                    {
                        firstSupported[id] = call.CanonicalId;
                    }
                }
            }
        }

        return symbols
            .Select(pair =>
            {
                (ForeignTypeEntry entry, bool declared) = Get(
                    pair.Value,
                    pair.Key);
                return new ForeignTypeUse(
                    pair.Key,
                    pair.Value.ToDisplayString(),
                    entry,
                    declared,
                    uses.GetValueOrDefault(pair.Key),
                    supported.GetValueOrDefault(pair.Key),
                    firstSupported.GetValueOrDefault(pair.Key));
            })
            .OrderByDescending(use => use.SupportedUses)
            .ThenByDescending(use => use.Uses)
            .ThenBy(use => use.CanonicalId, StringComparer.Ordinal)
            .ToArray();
    }

    private static IEnumerable<ITypeSymbol> GetSignatureTypes(
        ProjectedCall call)
    {
        yield return call.Symbol.ReturnType;
        foreach (IParameterSymbol parameter in call.Symbol.Parameters)
        {
            yield return parameter.Type;
        }
    }

    /// <summary>
    /// The foreign types reachable from one signature type. A type argument is
    /// as much a part of the signature as the constructed type is, so
    /// <c>ImmutableArray&lt;ISymbol&gt;</c> contributes the array and
    /// <c>IEnumerable&lt;KeyValuePair&lt;string, string&gt;&gt;</c>
    /// contributes three.
    /// </summary>
    private static IEnumerable<ITypeSymbol> Expand(
        ITypeSymbol type,
        IReadOnlySet<string> facadeAssemblies)
    {
        switch (type)
        {
            case ITypeParameterSymbol:
                yield break;
            case IArrayTypeSymbol array:
                foreach (ITypeSymbol nested in Expand(
                    array.ElementType,
                    facadeAssemblies))
                {
                    yield return nested;
                }

                yield break;
            case IPointerTypeSymbol pointer:
                yield return pointer;
                foreach (ITypeSymbol nested in Expand(
                    pointer.PointedAtType,
                    facadeAssemblies))
                {
                    yield return nested;
                }

                yield break;
        }

        if (type.ContainingAssembly is { } assembly &&
            !facadeAssemblies.Contains(assembly.Name))
        {
            yield return type;
        }

        if (type is not INamedTypeSymbol named)
        {
            yield break;
        }

        foreach (ITypeSymbol argument in named.TypeArguments)
        {
            foreach (ITypeSymbol nested in Expand(argument, facadeAssemblies))
            {
                yield return nested;
            }
        }
    }

    /// <summary>
    /// Deliberately excludes <c>IntPtr</c> and <c>UIntPtr</c>: they are
    /// primitive in the type system but address-sized values whose meaning is
    /// the producing module's, which is the opposite of bit-identical.
    /// </summary>
    private static bool IsPrimitive(ITypeSymbol type)
    {
        if (type.SpecialType is
            SpecialType.System_Void or
            SpecialType.System_Boolean or
            SpecialType.System_Char or
            SpecialType.System_SByte or
            SpecialType.System_Byte or
            SpecialType.System_Int16 or
            SpecialType.System_UInt16 or
            SpecialType.System_Int32 or
            SpecialType.System_UInt32 or
            SpecialType.System_Int64 or
            SpecialType.System_UInt64 or
            SpecialType.System_Single or
            SpecialType.System_Double or
            SpecialType.System_Decimal or
            SpecialType.System_String)
        {
            return true;
        }

        return type is INamedTypeSymbol
        {
            TypeKind: TypeKind.Enum,
            EnumUnderlyingType.SpecialType: not SpecialType.None,
        };
    }
}
