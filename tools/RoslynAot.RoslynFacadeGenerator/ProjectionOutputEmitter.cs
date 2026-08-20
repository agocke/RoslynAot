using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.DotNet.GenAPI;
using StaticCs;

namespace RoslynAot.RoslynFacadeGenerator;

internal static class ProjectionOutputEmitter
{
    public static void WriteNonFacadeOutputs(
        ProjectionModel model,
        string outputRoot)
    {
        string abiDirectory = RecreateDirectory(
            Path.Combine(outputRoot, "Abi"));
        string compilerDirectory = RecreateDirectory(
            Path.Combine(outputRoot, "Compiler"));
        string analyzerRuntimeDirectory = RecreateDirectory(
            Path.Combine(outputRoot, "AnalyzerRuntime"));
        string manifestDirectory = RecreateDirectory(
            Path.Combine(outputRoot, "Manifest"));

        WriteGeneratedFile(
            Path.Combine(abiDirectory, "RoslynControlVtbl.g.cs"),
            EmitAbiMetadata(model));
        IReadOnlyDictionary<string, int> callOrdinals =
            GetCallCounterOrdinals(model);
        foreach (VtblProjection vtbl in model.Vtbls)
        {
            WriteGeneratedFile(
                Path.Combine(
                    abiDirectory,
                    $"{vtbl.Name}.g.cs"),
                EmitAbiVtbl(vtbl));
            WriteGeneratedFile(
                Path.Combine(
                    compilerDirectory,
                    $"{GetDispatcherClassName(vtbl)}.g.cs"),
                EmitCompilerDispatcher(vtbl, callOrdinals));
        }
        WriteGeneratedFile(
            Path.Combine(
                compilerDirectory,
                "RoslynCallCounters.g.cs"),
            EmitCallCounters(model, callOrdinals));
        WriteGeneratedFile(
            Path.Combine(
                compilerDirectory,
                "RoslynDispatcherRegistry.g.cs"),
            EmitCompilerDispatcherRegistry(model));
        WriteGeneratedFile(
            Path.Combine(
                analyzerRuntimeDirectory,
                "RoslynProxyFactory.g.cs"),
            EmitAnalyzerRuntimeProxyFactory());
        File.WriteAllText(
            Path.Combine(
                manifestDirectory,
                "ProjectionInventory.txt"),
            EmitInventory(model).ReplaceLineEndings("\n"));
    }

    public static void WriteFacadeRuntime(
        ProjectionModel model,
        string facadesRoot)
    {
        string runtimeDirectory = Path.Combine(facadesRoot, "Runtime");
        Directory.CreateDirectory(runtimeDirectory);
        WriteGeneratedFile(
            Path.Combine(runtimeDirectory, "RoslynFacadeRuntime.g.cs"),
            EmitFacadeRuntime());
        WriteGeneratedFile(
            Path.Combine(runtimeDirectory, "RoslynVtblFactory.g.cs"),
            EmitVtblFactory(model));

        string coreDirectory = Path.Combine(
            facadesRoot,
            "Microsoft.CodeAnalysis");
        WriteGeneratedFile(
            Path.Combine(
                coreDirectory,
                "RoslynAotProjectionFriends.g.cs"),
            """
            [assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(
                "roslyn-aot-roslyn-projection-client, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
            [assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(
                "RoslynAot.AnalyzerRuntime, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
            """);
        WriteGeneratedFile(
            Path.Combine(
                coreDirectory,
                "RoslynAotTypeMap.g.cs"),
            EmitFacadeTypeMap(model, "Microsoft.CodeAnalysis"));
        WriteGeneratedFile(
            Path.Combine(
                facadesRoot,
                "Microsoft.CodeAnalysis.CSharp",
                "RoslynAotProjectionFriends.g.cs"),
            """
            [assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(
                "roslyn-aot-roslyn-projection-client, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
            [assembly: global::System.Runtime.CompilerServices.InternalsVisibleTo(
                "RoslynAot.AnalyzerRuntime, PublicKey=0024000004800000940000000602000000240000525341310004000001000100b5fc90e7027f67871e773a8fde8938c81dd402ba65b9201d60593e96c492651e889cc13f1415ebb53fac1131ae0bd333c5ee6021672d9718ea31a8aebd0da0072f25d87dba6fc90ffd598ed4da35e44c398c454307e8e33b8426143daec9f596836f97c8f74750e5975c64e2189f45def46b2a2b1247adc3652bf5c308055da9")]
            """);
        WriteGeneratedFile(
            Path.Combine(
                facadesRoot,
                "Microsoft.CodeAnalysis.CSharp",
                "RoslynAotTypeMap.g.cs"),
            EmitFacadeTypeMap(model, "Microsoft.CodeAnalysis.CSharp"));
        WriteGeneratedFile(
            Path.Combine(coreDirectory, "RoslynAotDerivedProxies.g.cs"),
            EmitDerivedProxyRegistrations(model, "Microsoft.CodeAnalysis"));
        WriteGeneratedFile(
            Path.Combine(
                facadesRoot,
                "Microsoft.CodeAnalysis.CSharp",
                "RoslynAotDerivedProxies.g.cs"),
            EmitDerivedProxyRegistrations(
                model,
                "Microsoft.CodeAnalysis.CSharp"));
    }

    /// <summary>
    /// Registers each projected class onto its base so that a proxy built at
    /// the base type resolves to the most-derived one.
    /// </summary>
    /// <remarks>
    /// Emitted per assembly and keyed on the <em>derived</em> type's assembly,
    /// because that is the only direction the facades reference each other:
    /// Microsoft.CodeAnalysis cannot name <c>CSharpParseOptions</c>, while
    /// Microsoft.CodeAnalysis.CSharp can reach <c>ParseOptions</c>' internals
    /// through the friend declaration Roslyn already ships. Four of the
    /// thirteen hierarchies cross assemblies this way, and they are exactly
    /// the language-specific ones — including the <c>ParseOptions</c> pair
    /// CA1507 casts through.
    /// </remarks>
    private static string EmitDerivedProxyRegistrations(
        ProjectionModel model,
        string assemblyName)
    {
        var registrations = new List<string>();
        foreach (TypeProjection type in model.Types
            .Where(type =>
                type.RequiresProxy &&
                !type.UsesDynamicInterfaceProxy &&
                type.Symbol.TypeKind == TypeKind.Class)
            .OrderBy(
                type => type.CanonicalId,
                StringComparer.Ordinal))
        {
            foreach (TypeProjection derived in model
                .GetProxiedDerivedTypes(type.Symbol)
                .Where(derived =>
                    derived.Symbol.ContainingAssembly.Name == assemblyName))
            {
                VtblProjection? vtbl = derived.InstanceVtbl;
                if (vtbl is null)
                {
                    continue;
                }

                if (type.InstanceVtbl is not { } baseVtbl)
                {
                    continue;
                }

                (long low, long high) = GetVtblIdParts(vtbl);
                (long baseLow, long baseHigh) = GetVtblIdParts(baseVtbl);
                string derivedName = derived.Symbol.ToDisplayString(
                    SymbolDisplayFormat.FullyQualifiedFormat);
                registrations.Add(
                    "global::RoslynAot.RoslynFacade." +
                    "RoslynDerivedProxyRegistry.Register(" +
                    $"{baseLow}L, {baseHigh}L, {low}L, {high}L, " +
                    $"{ProjectionModel.GetBaseDepth(derived.Symbol)}, " +
                    "static (controlVtbl, handle) => " +
                    $"{derivedName}.__RoslynAotCreateProxy(controlVtbl, handle));");
            }
        }

        var builder = new IndentingBuilder();
        builder.AppendLine("namespace RoslynAot.RoslynFacade;");
        builder.AppendLine("");
        builder.AppendLine("internal static class RoslynAotDerivedProxies");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine(
            "[global::System.Runtime.CompilerServices.ModuleInitializer]");
        builder.AppendLine("internal static void Register()");
        builder.AppendLine("{");
        builder.Indent();
        foreach (string registration in registrations)
        {
            builder.AppendLine(registration);
        }

        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string EmitFacadeTypeMap(
        ProjectionModel model,
        string assemblyName)
    {
        var builder = new IndentingBuilder();
        foreach (VtblProjection vtbl in GetDynamicInterfaceVtbls(model)
            .Where(
                vtbl =>
                    vtbl.FacadeType.ContainingAssembly.Name == assemblyName))
        {
            string typeName = vtbl.FacadeType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat);
            builder.AppendLine(
                "[assembly: global::System.Runtime.InteropServices." +
                "TypeMapAssociation<" +
                "global::RoslynAot.RoslynFacade.RoslynProxyTypeMap>(");
            builder.Indent();
            builder.AppendLine($"typeof({typeName}),");
            builder.AppendLine(
                $"typeof({typeName}.__RoslynAotImplementation))]");
            builder.Dedent();
        }

        return builder.ToString();
    }

    private static string EmitAbiMetadata(ProjectionModel model) =>
        $$"""
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;

        namespace RoslynAot.Abi;

        public static unsafe class RoslynAbi
        {
            public const int Success = 0;
            public const int InvalidArgument = unchecked((int)0x80070057);
            public const int ObjectDisposed = unchecked((int)0x80131622);
            public const int Unsupported = unchecked((int)0x80131515);
            public const int Failure = unchecked((int)0x80004005);

            public const string ManifestIdentity = "{{model.Identity}}";
            public const long ManifestIdentityLow = {{model.IdentityLow}}L;
            public const long ManifestIdentityHigh = {{model.IdentityHigh}}L;

            public static uint Release(nint instance)
            {
                if (instance == 0)
                {
                    return 0;
                }

                nint* vtable = *(nint**)instance;
                var release = (delegate* unmanaged<nint, uint>)vtable[2];
                return release(instance);
            }
        }

        public enum RoslynRemoteErrorKind
        {
            None,
            Argument,
            ObjectDisposed,
            Unsupported,
            OperationCanceled,
            Failure,
        }

        public enum RoslynWellKnownObject
        {
            SymbolEqualityComparerDefault,
            SymbolEqualityComparerIncludeNullability,
        }

        /// <summary>
        /// Which member of the constant union is on the wire.
        /// </summary>
        /// <remarks>
        /// <c>NoValue</c> and <c>Null</c> are separate members on purpose:
        /// <c>default(Optional&lt;object&gt;)</c> and an
        /// <c>Optional&lt;object&gt;</c> holding <c>null</c> are observably
        /// different, and an analyzer asking whether an expression is a
        /// constant is asking exactly that difference. <c>NoValue</c> is
        /// unreachable for a bare <c>object</c> return, which has no third
        /// state.
        ///
        /// Enum-typed constants do not appear here: Roslyn stores them as
        /// their underlying primitive, so they arrive as <c>Int32</c> and the
        /// analyzer's own cast re-types them. That is the managed behaviour,
        /// not a simplification.
        /// </remarks>
        public enum RoslynConstantKind
        {
            NoValue,
            Null,
            Boolean,
            SByte,
            Byte,
            Int16,
            UInt16,
            Int32,
            UInt32,
            Int64,
            UInt64,
            Char,
            Single,
            Double,
            Decimal,
            String,
            DateTime,
        }

        [GeneratedComInterface]
        [Guid("{{model.ControlVtblId:D}}")]
        public partial interface IRoslynControlVtbl
        {
            [PreserveSig]
            int GetManifestIdentity(
                out long identityLow,
                out long identityHigh);

            [PreserveSig]
            int GetVtbl(
                long vtblIdLow,
                long vtblIdHigh,
                out nint vtbl);

            [PreserveSig]
            int CopyLastErrorUtf16(
                nint buffer,
                int bufferLength,
                out int requiredLength,
                out RoslynRemoteErrorKind errorKind);

            [PreserveSig]
            int CreateSourceTextUtf16(
                nint utf16Text,
                int utf16Length,
                int checksumAlgorithm,
                out long result);

            [PreserveSig]
            int IsObjectType(
                long handle,
                long vtblIdLow,
                long vtblIdHigh,
                out int isType);

            [PreserveSig]
            int CreateObjectCollection(
                nint handles,
                int count,
                out long result);

            [PreserveSig]
            int GetCollectionCount(
                long handle,
                out int count);

            [PreserveSig]
            int GetObjectCollectionItem(
                long handle,
                int index,
                out long result);

            [PreserveSig]
            int CopyStringCollectionItemUtf16(
                long handle,
                int index,
                nint buffer,
                int bufferLength,
                out int requiredLength);

            // Membership must be answered by the collection that owns the
            // semantics. Copying the contents to a string[] and probing that
            // silently substitutes ordinal equality for whatever comparer the
            // source used - Roslyn's analyzer config keys, for one, are
            // case-insensitive through CaseInsensitiveComparison, so the copy
            // answers false where Roslyn answers true.
            [PreserveSig]
            int StringCollectionContains(
                long handle,
                [global::System.Runtime.InteropServices.Marshalling.MarshalUsing(typeof(global::System.Runtime.InteropServices.Marshalling.Utf16StringMarshaller))] string value,
                out int result);

            // Materializes a live collection into an indexable snapshot so
            // enumeration can use the index-based item accessor above. Only
            // enumeration pays for this; membership and count do not.
            [PreserveSig]
            int SnapshotStringCollection(
                long handle,
                out long result);

            [PreserveSig]
            int GetWellKnownObject(
                RoslynWellKnownObject kind,
                out long result);

            [PreserveSig]
            int SymbolEqualityComparerEquals(
                RoslynWellKnownObject kind,
                long x,
                long y,
                out int result);

            [PreserveSig]
            int SymbolEqualityComparerGetHashCode(
                RoslynWellKnownObject kind,
                long symbol,
                out int result);

            [PreserveSig]
            int CopyObjectToStringUtf16(
                long handle,
                nint buffer,
                int bufferLength,
                out int requiredLength);

            // A string constant arrives as a handle to the boxed string
            // alongside its tag, and is read back through this rather than
            // through CopyObjectToStringUtf16. The two would behave
            // identically today, because String.ToString is the identity, but
            // relying on that would make the transport depend on a BCL detail
            // instead of on GetObject<string>.
            // The most-derived projected type of the object behind a handle.
            // IsObjectType answers "is it this one" and needs a candidate;
            // an analyzer-side switch over a visitor's hundred-odd operation
            // interfaces needs the answer itself, in one crossing rather than
            // one per candidate.
            [PreserveSig]
            int GetObjectRuntimeVtblId(
                long handle,
                out long vtblIdLow,
                out long vtblIdHigh);

            [PreserveSig]
            int CopyConstantStringUtf16(
                long handle,
                nint buffer,
                int bufferLength,
                out int requiredLength);

            [PreserveSig]
            int ObjectEquals(
                long handle,
                long other,
                out int result);

            [PreserveSig]
            int ObjectGetHashCode(
                long handle,
                out int result);
        }
        """;

    private static string EmitAbiVtbl(VtblProjection vtbl)
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("using System.Runtime.InteropServices;");
        builder.AppendLine("using System.Runtime.InteropServices.Marshalling;");
        builder.AppendLine("");
        builder.AppendLine("namespace RoslynAot.Abi;");
        builder.AppendLine("");
        builder.AppendLine("[GeneratedComInterface]");
        builder.AppendLine($"[Guid(\"{vtbl.VtblId.ToString("D")}\")]");
        string baseVtbl = vtbl.BaseVtbl is null
            ? string.Empty
            : $" : {vtbl.BaseVtbl.Name}";
        builder.AppendLine(
            $"public partial interface {vtbl.Name}{baseVtbl}");
        builder.AppendLine("{");
        builder.Indent();
        foreach (ProjectedCall operation in vtbl.Members)
        {
            builder.AppendLine("");
            builder.AppendLine("[PreserveSig]");
            builder.AppendLine($"int {operation.GeneratedName}(");
            IReadOnlyList<string> abiParameters = GetAbiParameters(operation);
            builder.Indent();
            for (int index = 0; index < abiParameters.Count; index++)
            {
                builder.AppendLine(
                    abiParameters[index] +
                    (index + 1 == abiParameters.Count ? string.Empty : ","));
            }

            builder.Dedent();
            builder.AppendLine(");");
        }

        builder.Dedent();
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// Assigns each distinct projected Roslyn member a stable counter slot.
    /// Keyed on canonical signature, not generated name: a member inherited
    /// into several vtbls is emitted as several dispatcher methods but is one
    /// Roslyn member, and the coverage metric is about members.
    /// </summary>
    private static IReadOnlyDictionary<string, int> GetCallCounterOrdinals(
        ProjectionModel model)
    {
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (string signature in model.Vtbls
            .SelectMany(GetDispatcherMembers)
            .Select(operation => operation.CanonicalSignature)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(signature => signature, StringComparer.Ordinal))
        {
            ordinals[signature] = ordinals.Count;
        }

        return ordinals;
    }

    /// <summary>
    /// The control vtbl's own methods, which carry the traffic no per-member
    /// counter can see.
    /// </summary>
    /// <remarks>
    /// Dispatcher counters record one increment per projected member call, but
    /// a single such call can fan out into many control-vtbl crossings — a
    /// collection read costs a count plus two per element, so a member counted
    /// once was really hundreds of round trips. Leaving these uncounted made
    /// the boundary totals look like the whole picture when they were the tip
    /// of it, and a measurement that silently under-reports is worse than none.
    ///
    /// This list must match the interface emitted in <c>EmitAbi</c>; a name
    /// here that no longer exists there is a dead counter, and a method there
    /// missing here reads as zero calls rather than as uninstrumented.
    /// </remarks>
    private static readonly string[] s_controlVtblMembers =
    [
        "GetManifestIdentity",
        "GetVtbl",
        "CopyLastErrorUtf16",
        "CreateSourceTextUtf16",
        "IsObjectType",
        "CreateObjectCollection",
        "GetCollectionCount",
        "GetObjectCollectionItem",
        "CopyStringCollectionItemUtf16",
        "StringCollectionContains",
        "SnapshotStringCollection",
        "GetWellKnownObject",
        "SymbolEqualityComparerEquals",
        "SymbolEqualityComparerGetHashCode",
        "CopyObjectToStringUtf16",
        "GetObjectRuntimeVtblId",
        "CopyConstantStringUtf16",
        "ObjectEquals",
        "ObjectGetHashCode",
    ];

    private static string EmitCallCounters(
        ProjectionModel model,
        IReadOnlyDictionary<string, int> ordinals)
    {
        ProjectedCall[] members = model.Vtbls
            .SelectMany(GetDispatcherMembers)
            .GroupBy(
                operation => operation.CanonicalSignature,
                StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(
                operation => ordinals[operation.CanonicalSignature])
            .ToArray();

        var builder = new IndentingBuilder();
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System.Threading;");
        builder.AppendLine("");
        builder.AppendLine("namespace RoslynAot.Csc;");
        builder.AppendLine("");
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            "/// Per-member call counts at the projection boundary. Counting is");
        builder.AppendLine(
            "/// unconditional - one interlocked increment against a preallocated");
        builder.AppendLine(
            "/// slot, negligible beside the round trip it measures - so a count of");
        builder.AppendLine(
            "/// zero always means 'never called' rather than 'not instrumented'.");
        builder.AppendLine("/// </summary>");
        builder.AppendLine("internal static class RoslynCallCounters");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine(
            "public const int MemberCount = " +
            $"{members.Length + s_controlVtblMembers.Length};");
        builder.AppendLine("");
        builder.AppendLine(
            "private static readonly long[] s_counts = new long[MemberCount];");
        builder.AppendLine("");
        builder.AppendLine("public static readonly string[] MemberNames =");
        builder.AppendLine("[");
        builder.Indent();
        foreach (ProjectedCall operation in members)
        {
            builder.AppendLine(
                $"{Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(GetCounterName(operation), quote: true)},");
        }

        foreach (string name in s_controlVtblMembers)
        {
            builder.AppendLine(
                Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(
                    $"RoslynAot.Abi.IRoslynControlVtbl.{name}",
                    quote: true) +
                ",");
        }

        builder.Dedent();
        builder.AppendLine("];");
        builder.AppendLine("");
        builder.AppendLine("/// <summary>");
        builder.AppendLine(
            "/// Ordinals for the control vtbl, which is hand-written and");
        builder.AppendLine(
            "/// so records against these rather than an emitted literal.");
        builder.AppendLine("/// </summary>");
        for (int index = 0; index < s_controlVtblMembers.Length; index++)
        {
            builder.AppendLine(
                $"public const int Control{s_controlVtblMembers[index]} = " +
                $"{members.Length + index};");
        }

        builder.AppendLine("");
        builder.AppendLine("public static void Record(int ordinal) =>");
        builder.Indent();
        builder.AppendLine("Interlocked.Increment(ref s_counts[ordinal]);");
        builder.Dedent();
        builder.AppendLine("");
        builder.AppendLine("public static long[] Snapshot()");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("var snapshot = new long[MemberCount];");
        builder.AppendLine(
            "for (int index = 0; index < snapshot.Length; index++)");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine(
            "snapshot[index] = Interlocked.Read(ref s_counts[index]);");
        builder.Dedent();
        builder.AppendLine("}");
        builder.AppendLine("");
        builder.AppendLine("return snapshot;");
        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// The member name shape the differential harness already parses out of
    /// AD0001 stack frames, so coverage rows and burn-down reasons join.
    /// </summary>
    private static string GetCounterName(ProjectedCall operation) =>
        $"{operation.Symbol.ContainingType.ToDisplayString()}." +
        $"{operation.Symbol.Name}";

    private static string EmitCompilerDispatcher(
        VtblProjection vtbl,
        IReadOnlyDictionary<string, int> ordinals)
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System.Text;");
        builder.AppendLine("using System.Runtime.InteropServices.Marshalling;");
        builder.AppendLine("using RoslynAot.Abi;");
        builder.AppendLine("");
        builder.AppendLine("namespace RoslynAot.Csc;");
        builder.AppendLine("");
        builder.AppendLine("[GeneratedComClass]");
        builder.AppendLine(
            $"internal sealed partial class {GetDispatcherClassName(vtbl)} : {vtbl.Name}");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("private readonly RoslynInterop _owner;");
        builder.AppendLine("");
        builder.AppendLine(
            $"public {GetDispatcherClassName(vtbl)}(RoslynInterop owner)");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine(
            "_owner = owner ?? throw new ArgumentNullException(nameof(owner));");
        builder.Dedent();
        builder.AppendLine("}");

        foreach (ProjectedCall operation in GetDispatcherMembers(vtbl))
        {
            EmitCompilerMethod(
                builder,
                operation,
                ordinals[operation.CanonicalSignature]);
        }

        builder.Dedent();
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string EmitCompilerDispatcherRegistry(
        ProjectionModel model)
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("namespace RoslynAot.Csc;");
        builder.AppendLine("");
        builder.AppendLine("internal static class RoslynDispatcherRegistry");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("public static object Create(");
        builder.Indent();
        builder.AppendLine("long vtblIdLow,");
        builder.AppendLine("long vtblIdHigh,");
        builder.AppendLine("RoslynInterop owner) =>");
        builder.AppendLine("(vtblIdLow, vtblIdHigh) switch");
        builder.AppendLine("{");
        builder.Indent();
        foreach (VtblProjection vtbl in model.Vtbls)
        {
            (long low, long high) = GetVtblIdParts(vtbl);
            builder.AppendLine(
                $"({low}L, {high}L) => new {GetDispatcherClassName(vtbl)}(owner),");
        }

        builder.AppendLine(
            "_ => throw new PlatformNotSupportedException(\"The requested Roslyn vtable is not available in this build.\"),");
        builder.Dedent();
        builder.AppendLine("};");
        builder.Dedent();
        builder.AppendLine("");
        builder.AppendLine("public static bool IsRuntimeType(");
        builder.Indent();
        builder.AppendLine("object value,");
        builder.AppendLine("long vtblIdLow,");
        builder.AppendLine("long vtblIdHigh) =>");
        builder.AppendLine("(vtblIdLow, vtblIdHigh) switch");
        builder.AppendLine("{");
        builder.Indent();
        foreach (VtblProjection vtbl in GetRuntimeClassVtbls(model))
        {
            (long low, long high) = GetVtblIdParts(vtbl);
            string typeName = vtbl.FacadeType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat);
            builder.AppendLine(
                $"({low}L, {high}L) => value is {typeName},");
        }

        builder.AppendLine("_ => false,");
        builder.Dedent();
        builder.AppendLine("};");
        builder.Dedent();
        builder.AppendLine("");

        // The most-derived projected type of a live object, which is what an
        // analyzer-side switch needs to dispatch on in one crossing instead of
        // probing every candidate. Cached per runtime type: Roslyn's operation
        // and symbol implementations are a bounded set, and a walker asks this
        // once per node.
        builder.AppendLine(
            "private static readonly global::System.Collections.Generic." +
            "Dictionary<global::System.Type, (long Low, long High)> " +
            "s_runtimeVtblIds = [];");
        builder.AppendLine("");
        builder.AppendLine(
            """
            public static bool TryGetRuntimeVtblId(
                object value,
                out long vtblIdLow,
                out long vtblIdHigh)
            {
                global::System.Type type = value.GetType();
                (long Low, long High) resolved;
                lock (s_runtimeVtblIds)
                {
                    if (!s_runtimeVtblIds.TryGetValue(type, out resolved))
                    {
                        resolved = ResolveRuntimeVtblId(value);
                        s_runtimeVtblIds[type] = resolved;
                    }
                }

                vtblIdLow = resolved.Low;
                vtblIdHigh = resolved.High;
                return resolved != (0L, 0L);
            }
            """);
        builder.AppendLine("");
        builder.AppendLine(
            "private static (long, long) ResolveRuntimeVtblId(object value)");
        builder.AppendLine("{");
        builder.Indent();
        foreach (VtblProjection vtbl in GetRuntimeClassVtbls(model))
        {
            (long low, long high) = GetVtblIdParts(vtbl);
            string typeName = vtbl.FacadeType.ToDisplayString(
                SymbolDisplayFormat.FullyQualifiedFormat);
            builder.AppendLine(
                $"if (value is {typeName}) return ({low}L, {high}L);");
        }

        builder.AppendLine("return (0L, 0L);");
        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// Fixed text: the projection model has no say in this file's contents.
    /// </summary>
    private static string EmitAnalyzerRuntimeProxyFactory() =>
        """
        using System.Runtime.CompilerServices;
        using System.Runtime.InteropServices;
        using RoslynAot.Abi;
        using RoslynAot.RoslynFacade;
        using Microsoft.CodeAnalysis;

        namespace RoslynAot.AnalyzerRuntime;

        internal static class RoslynProxyFactory
        {
            public static SyntaxNode CreateSyntaxNode(
                IRoslynControlVtbl controlVtbl,
                long handle) =>
                SyntaxNode.__RoslynAotCreateProxy(controlVtbl, handle);
        }

        """;

    private static IEnumerable<VtblProjection> GetDynamicInterfaceVtbls(
        ProjectionModel model) =>
        model.Vtbls
            .Where(
                vtbl =>
                    !vtbl.IsTypeVtbl &&
                    model.UsesDynamicInterfaceProxy(vtbl.FacadeType))
            .OrderBy(
                vtbl => CanonicalSignatureBuilder.GetMetadataTypeName(
                    vtbl.FacadeType),
                StringComparer.Ordinal);

    private static IEnumerable<VtblProjection> GetRuntimeClassVtbls(
        ProjectionModel model) =>
        model.Vtbls
            .Where(
                vtbl =>
                    !vtbl.IsTypeVtbl &&
                    vtbl.FacadeType.TypeKind is
                        TypeKind.Class or
                        TypeKind.Interface &&
                    IsExternallyReferenceable(vtbl.FacadeType))
            .OrderByDescending(vtbl => GetSpecificity(vtbl.FacadeType))
            .ThenBy(
                vtbl => CanonicalSignatureBuilder.GetMetadataTypeName(
                    vtbl.FacadeType),
                StringComparer.Ordinal);

    /// <summary>
    /// How specific a type is, so that "most derived first" is a total order
    /// over classes and interfaces alike.
    /// </summary>
    /// <remarks>
    /// Counting only the base-type chain ranks every interface equally at one,
    /// because an interface has no base type — which left the order among them
    /// alphabetical. That is harmless for a yes/no <c>IsRuntimeType</c> query
    /// and wrong for a most-derived one, where it would answer
    /// <c>IOperation</c> for an object that is an <c>IBinaryOperation</c>.
    /// Adding the transitive interface count separates them: a derived
    /// interface implements everything its bases do and at least one more.
    /// </remarks>
    private static int GetSpecificity(INamedTypeSymbol type)
    {
        int depth = type.AllInterfaces.Length;
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.BaseType)
        {
            depth++;
        }

        return depth;
    }

    private static bool IsExternallyReferenceable(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type;
             current is not null;
             current = current.ContainingType)
        {
            if (current.DeclaredAccessibility != Accessibility.Public)
            {
                return false;
            }
        }

        return true;
    }

    private static IReadOnlyList<ProjectedCall> GetDispatcherMembers(
        VtblProjection vtbl)
    {
        var hierarchy = new Stack<VtblProjection>();
        for (VtblProjection? current = vtbl;
             current is not null;
             current = current.BaseVtbl)
        {
            hierarchy.Push(current);
        }

        return hierarchy
            .SelectMany(current => current.Members)
            .GroupBy(
                operation => operation.GeneratedName,
                StringComparer.Ordinal)
            .Select(group => group.Last())
            .ToArray();
    }

    private static string GetDispatcherClassName(VtblProjection vtbl) =>
        $"{(vtbl.Name.StartsWith('I') ? vtbl.Name[1..] : vtbl.Name)}Dispatcher";

    private static (long Low, long High) GetVtblIdParts(
        VtblProjection vtbl)
    {
        byte[] bytes = vtbl.VtblId.ToByteArray();
        return (
            BitConverter.ToInt64(bytes, 0),
            BitConverter.ToInt64(bytes, 8));
    }

    private static void EmitCompilerMethod(
        IndentingBuilder builder,
        ProjectedCall operation,
        int counterOrdinal)
    {
        builder.AppendLine("");
        builder.AppendLine(
            $"public{(operation.ReturnValue.Kind == AbiTypeKind.Utf16String ? " unsafe" : string.Empty)} int {operation.GeneratedName}(");
        IReadOnlyList<string> abiParameters = GetAbiParameters(operation);
        builder.Indent();
        for (int index = 0; index < abiParameters.Count; index++)
        {
            builder.AppendLine(
                abiParameters[index] +
                (index + 1 == abiParameters.Count ? ")" : ","));
        }

        builder.Dedent();
        builder.AppendLine("{");
        builder.Indent();

        // Counted before the try, so a member that always throws still reports
        // as called. Coverage answers "was it reached", not "did it succeed".
        builder.AppendLine($"RoslynCallCounters.Record({counterOrdinal});");
        if (operation.ReturnValue.Kind == AbiTypeKind.Utf16String)
        {
            builder.AppendLine("requiredLength = default;");
        }
        else if (IsConstant(operation.ReturnValue.Kind))
        {
            builder.AppendLine("constantKind = default;");
            builder.AppendLine("constantLow = default;");
            builder.AppendLine("constantHigh = default;");
        }
        else if (operation.ReturnValue.Kind != AbiTypeKind.Void)
        {
            builder.AppendLine("result = default;");
        }

        builder.AppendLine("");
        builder.AppendLine("try");
        builder.AppendLine("{");
        builder.Indent();
        foreach (string statement in GetCompilerStatements(operation))
        {
            builder.AppendLine(statement);
        }

        builder.AppendLine("return RoslynAbi.Success;");
        builder.Dedent();
        builder.AppendLine("}");
        builder.AppendLine("catch (global::System.Exception exception)");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine("return _owner.SetError(exception);");
        builder.Dedent();
        builder.AppendLine("}");
        builder.Dedent();
        builder.AppendLine("}");
    }

    private static IEnumerable<string> GetCompilerStatements(
        ProjectedCall operation)
    {
        if (operation.Strategy == ProjectionStrategy.Dispose)
        {
            yield return
                $"_owner.Objects.DisposeObject<{GetSourceType(operation.Symbol.ContainingType)}>" +
                "(receiver);";
            yield break;
        }

        if (operation.Strategy == ProjectionStrategy.PropertySet)
        {
            var property =
                (IPropertySymbol)operation.Symbol.AssociatedSymbol!;
            string receiver = GetCompilerReceiver(operation);
            string value = GetCompilerArgument(
                operation.Parameters[^1]);
            if (property.IsIndexer)
            {
                string indexes = string.Join(
                    ", ",
                    operation.Parameters
                        .Take(operation.Parameters.Count - 1)
                        .Select(GetCompilerArgument));
                yield return $"{receiver}[{indexes}] = {value};";
            }
            else
            {
                yield return $"{receiver}." +
                    $"{CSharpName.EscapeIdentifier(property.Name)} = " +
                    $"{value};";
            }

            yield break;
        }

        string invocation = GetCompilerInvocation(operation);
        if (operation.ReturnValue.Kind == AbiTypeKind.Utf16String)
        {
            yield return
                "if (bufferLength < 0) throw new global::System.ArgumentOutOfRangeException(nameof(bufferLength));";
            yield return $"string? __roslynAotValue = {invocation};";
            yield return
                "if (__roslynAotValue is null) { requiredLength = -1; return RoslynAbi.Success; }";
            yield return
                "requiredLength = __roslynAotValue.Length;";
            yield return "if (buffer == 0) return RoslynAbi.Success;";
            yield return
                "if (bufferLength < requiredLength) throw new global::System.ArgumentException(\"The UTF-16 result buffer is too small.\", nameof(bufferLength));";
            yield return
                "__roslynAotValue.AsSpan().CopyTo(new global::System.Span<char>((void*)buffer, bufferLength));";
            yield break;
        }

        switch (operation.ReturnValue.Kind)
        {
            case AbiTypeKind.Void:
                yield return $"{invocation};";
                break;
            case AbiTypeKind.ConstantUnion:
                yield return
                    $"_owner.WriteConstant({invocation}, " +
                    "out constantKind, out constantLow, out constantHigh);";
                break;
            case AbiTypeKind.OptionalConstant:
                yield return
                    $"var __roslynAotOptional = {invocation};";
                yield return
                    "if (__roslynAotOptional.HasValue) " +
                    "_owner.WriteConstant(__roslynAotOptional.Value, " +
                    "out constantKind, out constantLow, out constantHigh); " +
                    "else constantKind = RoslynConstantKind.NoValue;";
                break;
            case AbiTypeKind.Boolean:
                yield return $"result = {invocation} ? 1 : 0;";
                break;
            case AbiTypeKind.Enum:
                yield return
                    $"result = ({operation.ReturnValue.AbiType}){invocation};";
                break;
            case AbiTypeKind.Integral
                when operation.ReturnValue.SourceType.SpecialType ==
                    SpecialType.System_Char:
                yield return $"result = (ushort){invocation};";
                break;
            case AbiTypeKind.Integral:
                yield return $"result = {invocation};";
                break;
            case AbiTypeKind.StringCollection:
                // Deliberately not ToArray'd: the analyzer reaches this
                // collection through a handle so that membership is answered
                // by the collection itself, with its own comparer and its own
                // complexity, rather than by a copy that has neither.
                yield return
                    $"result = _owner.Objects.AddObject({invocation});";
                break;
            case AbiTypeKind.ObjectCollection:
                yield return
                    $"result = _owner.Objects.AddObject(" +
                    $"global::System.Linq.Enumerable.Cast<object>({invocation}).ToArray());";
                break;
            case AbiTypeKind.ObjectHandle:
                yield return operation.ReturnValue.IsNullable
                    ? $"result = _owner.Objects.AddNullableObject({invocation});"
                    : $"result = _owner.Objects.AddObject({invocation});";
                break;
            case AbiTypeKind.ValueHandle:
                yield return $"result = _owner.Objects.AddValue({invocation});";
                break;
            case AbiTypeKind.NullableHandle:
                yield return
                    $"result = {invocation} is {{ }} value " +
                    "? _owner.Objects.AddValue(value) : 0;";
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported compiler result for " +
                    $"'{operation.CanonicalSignature}'.");
        }
    }

    internal static bool IsConstant(AbiTypeKind kind) =>
        kind is AbiTypeKind.ConstantUnion or AbiTypeKind.OptionalConstant;

    private static string GetCompilerInvocation(
        ProjectedCall operation)
    {
        IMethodSymbol method = operation.Symbol;
        string receiver = GetCompilerReceiver(operation);
        string[] arguments = operation.Parameters
            .Select(GetCompilerArgument)
            .ToArray();

        if (operation.Strategy == ProjectionStrategy.Constructor)
        {
            return $"new {GetSourceType(method.ContainingType)}" +
                $"({string.Join(", ", arguments)})";
        }

        if (operation.Strategy == ProjectionStrategy.PropertyGet)
        {
            var property =
                (IPropertySymbol)method.AssociatedSymbol!;
            return property.IsIndexer
                ? $"{receiver}[{string.Join(", ", arguments)}]"
                : $"{receiver}." +
                    CSharpName.EscapeIdentifier(property.Name);
        }

        return $"{receiver}." +
            $"{CSharpName.EscapeIdentifier(method.Name)}" +
            $"({string.Join(", ", arguments)})";
    }

    private static string GetCompilerReceiver(
        ProjectedCall operation)
    {
        if (!operation.HasReceiver)
        {
            return GetSourceType(operation.Symbol.ContainingType);
        }

        AbiTypePlan receiver = operation.Receiver
            ?? throw new InvalidOperationException(
                "Instance operation has no receiver ABI plan.");
        return receiver.Kind switch
        {
            AbiTypeKind.ObjectHandle =>
                $"_owner.Objects.GetObject<{GetSourceType(operation.Symbol.ContainingType)}>(receiver)",
            AbiTypeKind.ValueHandle =>
                $"_owner.Objects.GetValue<{GetSourceType(operation.Symbol.ContainingType)}>(receiver)",
            _ => throw new InvalidOperationException(
                $"Unsupported receiver plan '{receiver.Kind}'."),
        };
    }

    private static string GetCompilerArgument(ParameterProjection parameter)
    {
        string name = CSharpName.EscapeIdentifier(
            parameter.Symbol.Name);
        return parameter.AbiType.Kind switch
        {
            AbiTypeKind.Boolean => $"{name} != 0",
            AbiTypeKind.Enum =>
                $"({GetSourceType(parameter.Symbol.Type)}){name}",
            AbiTypeKind.Integral
                when parameter.Symbol.Type.SpecialType ==
                    SpecialType.System_Char =>
                $"(char){name}",
            AbiTypeKind.Integral => name,
            AbiTypeKind.ObjectHandle =>
                parameter.AbiType.IsNullable
                    ? $"{name} == 0 ? null : " +
                    $"_owner.Objects.GetObject<{GetSourceType(parameter.AbiType.RemoteType!)}>({name})"
                    : $"_owner.Objects.GetObject<{GetSourceType(parameter.AbiType.RemoteType!)}>({name})",
            AbiTypeKind.ValueHandle =>
                $"_owner.Objects.GetValue<{GetSourceType(parameter.Symbol.Type)}>({name})",
            AbiTypeKind.NullableHandle =>
                $"{name} == 0 ? null : " +
                $"_owner.Objects.GetValue<{GetSourceType(parameter.AbiType.RemoteType!)}>({name})",
            AbiTypeKind.ObjectArray =>
                $"global::System.Array.ConvertAll(" +
                $"_owner.Objects.GetObject<object[]>({name}), " +
                $"static value => ({GetSourceType(((IArrayTypeSymbol)parameter.Symbol.Type).ElementType)})value)",
            AbiTypeKind.Utf16String => name,
            _ => throw new InvalidOperationException(
                $"Unsupported compiler argument '{parameter.Symbol}'."),
        };
    }

    private static IReadOnlyList<string> GetAbiParameters(
        ProjectedCall operation)
    {
        var parameters = new List<string>();
        if (operation.HasReceiver)
        {
            parameters.Add(
                $"{operation.Receiver!.AbiType} receiver");
        }

        parameters.AddRange(operation.Parameters.Select(parameter =>
            parameter.AbiType.Kind == AbiTypeKind.Utf16String
                ? "[global::System.Runtime.InteropServices.Marshalling.MarshalUsing(" +
                    "typeof(global::System.Runtime.InteropServices.Marshalling.Utf16StringMarshaller))] " +
                    "string " +
                    CSharpName.EscapeIdentifier(parameter.Symbol.Name)
                : $"{parameter.AbiType.AbiType} " +
                    CSharpName.EscapeIdentifier(parameter.Symbol.Name)));

        if (operation.ReturnValue.Kind == AbiTypeKind.Utf16String)
        {
            parameters.Add("nint buffer");
            parameters.Add("int bufferLength");
            parameters.Add("out int requiredLength");
        }
        else if (IsConstant(operation.ReturnValue.Kind))
        {
            // Two words of payload rather than one, because decimal is
            // sixteen bytes and truncating it would be a silent wrong answer
            // in exactly the transport whose job is to carry constants
            // exactly.
            parameters.Add("out RoslynConstantKind constantKind");
            parameters.Add("out long constantLow");
            parameters.Add("out long constantHigh");
        }
        else if (operation.ReturnValue.Kind != AbiTypeKind.Void)
        {
            parameters.Add($"out {operation.ReturnValue.AbiType} result");
        }

        return parameters;
    }

    private static string EmitFacadeRuntime()
    {
        return """
        using System.Runtime.InteropServices;
        using System.Runtime.InteropServices.Marshalling;
        using System.Threading;
        using RoslynAot.Abi;

        namespace RoslynAot.RoslynFacade;

        public delegate int CopyUtf16String(
            nint buffer,
            int bufferLength,
            out int requiredLength);

        public sealed class RoslynProxyTypeMap;

        /// <summary>
        /// Which projected classes derive from which, and how to build the
        /// most-derived proxy over a handle.
        /// </summary>
        /// <remarks>
        /// A cast to a class is a runtime type check, so unlike an interface
        /// cast there is no point at which the facade could resolve a derived
        /// type on demand. The proxy therefore has to be the most-derived type
        /// at the moment it is constructed.
        ///
        /// The table lives here, in the runtime assembly, keyed by the base
        /// type's vtbl id — deliberately not as a static on the base facade
        /// type itself. Registering onto the facade type would run its class
        /// constructor during module initialization, and several of these
        /// bases have static fields that are projected as throwing:
        /// <c>SyntaxTree.EmptyDiagnosticOptions</c> takes down the process
        /// before <c>Main</c>. Keying on the vtbl id touches nothing but this
        /// class.
        ///
        /// Registrations are emitted per assembly by whichever one owns the
        /// derived type, because that is the only direction the facades
        /// reference each other in: Microsoft.CodeAnalysis cannot name
        /// <c>CSharpParseOptions</c>.
        /// </remarks>
        public static class RoslynDerivedProxyRegistry
        {
            private sealed record Entry(
                long DerivedVtblIdLow,
                long DerivedVtblIdHigh,
                int Depth,
                Func<IRoslynControlVtbl, long, object> Create);

            private static readonly Dictionary<(long, long), Entry[]>
                s_entries = [];

            public static void Register(
                long baseVtblIdLow,
                long baseVtblIdHigh,
                long derivedVtblIdLow,
                long derivedVtblIdHigh,
                int depth,
                Func<IRoslynControlVtbl, long, object> create)
            {
                var entry = new Entry(
                    derivedVtblIdLow,
                    derivedVtblIdHigh,
                    depth,
                    create);
                lock (s_entries)
                {
                    (long, long) key = (baseVtblIdLow, baseVtblIdHigh);
                    Entry[] existing = s_entries.TryGetValue(
                        key,
                        out Entry[]? found)
                        ? found
                        : [];

                    // Most-derived first, so a grandchild is preferred over
                    // its parent when both answer IsObjectType.
                    Entry[] updated = [.. existing, entry];
                    Array.Sort(
                        updated,
                        static (left, right) =>
                            right.Depth.CompareTo(left.Depth));
                    s_entries[key] = updated;
                }
            }

            public static object? TryCreate(
                IRoslynControlVtbl controlVtbl,
                long handle,
                long baseVtblIdLow,
                long baseVtblIdHigh)
            {
                Entry[]? entries;
                lock (s_entries)
                {
                    if (!s_entries.TryGetValue(
                            (baseVtblIdLow, baseVtblIdHigh),
                            out entries))
                    {
                        return null;
                    }
                }

                foreach (Entry entry in entries)
                {
                    if (RoslynFacadeRuntime.IsObjectType(
                            controlVtbl,
                            handle,
                            entry.DerivedVtblIdLow,
                            entry.DerivedVtblIdHigh))
                    {
                        return entry.Create(controlVtbl, handle);
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// A compiler-owned string collection, reached through its handle
        /// rather than copied.
        /// </summary>
        /// <remarks>
        /// Copying the contents into a <c>string[]</c> and handing that back
        /// preserves the elements but discards the collection's behaviour.
        /// Two things go missing, and only one of them is benign:
        /// <list type="bullet">
        /// <item>Membership complexity. Roslyn backs these with sets -
        /// <c>IdentifierCollection</c>, <c>ImmutableSegmentedHashSet</c> -
        /// so an O(1) lookup becomes an O(n) array scan.</item>
        /// <item>Equality semantics. The copy answers with
        /// <c>EqualityComparer&lt;string&gt;.Default</c> no matter what the
        /// source used. Analyzer config keys compare case-insensitively
        /// through <c>CaseInsensitiveComparison</c>, so a copy answers
        /// <c>false</c> where Roslyn answers <c>true</c> - a wrong answer
        /// with no exception attached.</item>
        /// </list>
        /// Implementing <see cref="ICollection{T}"/> rather than only
        /// <see cref="IEnumerable{T}"/> is load-bearing:
        /// <c>Enumerable.Contains</c> defers to <c>ICollection&lt;T&gt;</c>
        /// when the runtime type provides it, so members declared as
        /// <c>IEnumerable&lt;string&gt;</c> get the faithful answer too.
        /// </remarks>
        public sealed class RoslynStringCollection : ICollection<string>
        {
            private readonly IRoslynControlVtbl _controlVtbl;
            private readonly long _handle;
            private string[]? _snapshot;

            public RoslynStringCollection(
                IRoslynControlVtbl controlVtbl,
                long handle)
            {
                _controlVtbl = controlVtbl ??
                    throw new ArgumentNullException(nameof(controlVtbl));
                _handle = handle != 0
                    ? handle
                    : throw new ArgumentOutOfRangeException(nameof(handle));
            }

            public int Count
            {
                get
                {
                    int status = _controlVtbl.GetCollectionCount(
                        _handle,
                        out int count);
                    RoslynFacadeRuntime.ThrowIfFailed(_controlVtbl, status);
                    return count;
                }
            }

            public bool IsReadOnly => true;

            public bool Contains(string item)
            {
                if (item is null)
                {
                    return false;
                }

                int status = _controlVtbl.StringCollectionContains(
                    _handle,
                    item,
                    out int result);
                RoslynFacadeRuntime.ThrowIfFailed(_controlVtbl, status);
                return result != 0;
            }

            // Enumeration is the one operation that cannot be answered a
            // question at a time, so it snapshots. Held afterwards because
            // the compiler-side collection a handle refers to is immutable
            // for the handle's lifetime.
            private string[] Snapshot()
            {
                string[]? snapshot = _snapshot;
                if (snapshot is null)
                {
                    int status = _controlVtbl.SnapshotStringCollection(
                        _handle,
                        out long snapshotHandle);
                    RoslynFacadeRuntime.ThrowIfFailed(_controlVtbl, status);
                    snapshot = RoslynFacadeRuntime.ReadStringCollection(
                        _controlVtbl,
                        snapshotHandle);
                    _snapshot = snapshot;
                }

                return snapshot;
            }

            public IEnumerator<string> GetEnumerator() =>
                ((IEnumerable<string>)Snapshot()).GetEnumerator();

            System.Collections.IEnumerator
                System.Collections.IEnumerable.GetEnumerator() =>
                GetEnumerator();

            public void CopyTo(string[] array, int arrayIndex) =>
                Snapshot().CopyTo(array, arrayIndex);

            public void Add(string item) =>
                throw new NotSupportedException();

            public bool Remove(string item) =>
                throw new NotSupportedException();

            public void Clear() =>
                throw new NotSupportedException();
        }

        public sealed class RoslynObjectProxy : IDynamicInterfaceCastable
        {
            public RoslynObjectProxy(
                IRoslynControlVtbl controlVtbl,
                long handle)
            {
                ControlVtbl = controlVtbl ??
                    throw new ArgumentNullException(nameof(controlVtbl));
                Handle = handle != 0
                    ? handle
                    : throw new ArgumentOutOfRangeException(nameof(handle));
            }

            private int? _hashCode;

            public IRoslynControlVtbl ControlVtbl { get; }

            private long Handle { get; }

            public long GetHandle() => Handle;

            private static readonly Lock s_cacheGate = new();
            private static readonly Dictionary<long, WeakReference<RoslynObjectProxy>>
                s_cache = [];
            private static int s_cacheInsertsSinceSweep;
            private const int CacheSweepInterval = 4096;

            /// <summary>
            /// Resolves a handle to the same proxy every time, so a Roslyn
            /// object read through two different interface accessors (say,
            /// as <c>ISymbol</c> and later as <c>ITypeSymbol</c>) comes back
            /// as the literal same instance — correct here because
            /// <see cref="IDynamicInterfaceCastable"/> lets one proxy answer
            /// any interface cast dynamically, so unlike the per-declaring-
            /// type proxies used for class hierarchies, there is nothing
            /// static-type-specific baked into this object to make distinct
            /// instances necessary. Weak-valued: the compiler process keeps
            /// every crossed object alive for good (see the reverse map in
            /// RoslynHandleTable), but an analyzer module that has moved on
            /// from a compilation should still be able to reclaim its own
            /// proxies for it.
            /// </summary>
            public static RoslynObjectProxy GetOrCreate(
                IRoslynControlVtbl controlVtbl,
                long handle)
            {
                lock (s_cacheGate)
                {
                    if (s_cache.TryGetValue(
                            handle,
                            out WeakReference<RoslynObjectProxy>? entry) &&
                        entry.TryGetTarget(out RoslynObjectProxy? existing))
                    {
                        return existing;
                    }

                    var proxy = new RoslynObjectProxy(controlVtbl, handle);
                    s_cache[handle] = new WeakReference<RoslynObjectProxy>(proxy);
                    if (++s_cacheInsertsSinceSweep >= CacheSweepInterval)
                    {
                        SweepDeadCacheEntries();
                    }

                    return proxy;
                }
            }

            // Must run under s_cacheGate: bounds the dictionary's own growth
            // against a long-lived analyzer module, since a dead
            // WeakReference's entry is otherwise never removed on its own.
            private static void SweepDeadCacheEntries()
            {
                s_cacheInsertsSinceSweep = 0;
                List<long>? dead = null;
                foreach (KeyValuePair<long, WeakReference<RoslynObjectProxy>> pair
                    in s_cache)
                {
                    if (!pair.Value.TryGetTarget(out _))
                    {
                        (dead ??= []).Add(pair.Key);
                    }
                }

                if (dead is not null)
                {
                    foreach (long key in dead)
                    {
                        s_cache.Remove(key);
                    }
                }
            }

            public override string ToString() =>
                RoslynFacadeRuntime.ReadUtf16String(
                    ControlVtbl,
                    (nint buffer, int bufferLength, out int requiredLength) =>
                        ControlVtbl.CopyObjectToStringUtf16(
                            Handle,
                            buffer,
                            bufferLength,
                            out requiredLength)) ?? string.Empty;

            public override bool Equals(object? other)
            {
                if (ReferenceEquals(this, other))
                {
                    return true;
                }

                if (other is not RoslynObjectProxy proxy)
                {
                    return false;
                }

                // A handle denotes one object for the single control this
                // module talks to, so equal handles are the same object and
                // GetOrCreate has usually already collapsed them to one
                // instance above. Distinct handles can still denote equal
                // objects, which only the compiler can decide.
                if (Handle == proxy.Handle)
                {
                    return true;
                }

                int status = ControlVtbl.ObjectEquals(
                    Handle,
                    proxy.Handle,
                    out int equal);
                RoslynFacadeRuntime.ThrowIfFailed(ControlVtbl, status);
                return equal != 0;
            }

            public override int GetHashCode()
            {
                int? cached = _hashCode;
                if (cached is null)
                {
                    int status = ControlVtbl.ObjectGetHashCode(
                        Handle,
                        out int hashCode);
                    RoslynFacadeRuntime.ThrowIfFailed(ControlVtbl, status);
                    cached = hashCode;
                    _hashCode = cached;
                }

                return cached.GetValueOrDefault();
            }

            public bool IsInterfaceImplemented(
                RuntimeTypeHandle interfaceType,
                bool throwIfNotImplemented)
            {
                if (!TryGetImplementationType(
                        interfaceType,
                        out Type? implementationType))
                {
                    return false;
                }

                byte[] bytes = implementationType.GUID.ToByteArray();
                int status = ControlVtbl.IsObjectType(
                    Handle,
                    BitConverter.ToInt64(bytes, 0),
                    BitConverter.ToInt64(bytes, 8),
                    out int isType);
                RoslynFacadeRuntime.ThrowIfFailed(ControlVtbl, status);
                return isType != 0;
            }

            public RuntimeTypeHandle GetInterfaceImplementation(
                RuntimeTypeHandle interfaceType)
            {
                if (!TryGetImplementationType(
                        interfaceType,
                        out Type? implementationType))
                {
                    throw new InvalidCastException(
                        Type.GetTypeFromHandle(interfaceType).FullName);
                }

                return implementationType.TypeHandle;
            }

            private static bool TryGetImplementationType(
                RuntimeTypeHandle interfaceType,
                out Type? implementationType)
            {
                Type requestedType = Type.GetTypeFromHandle(interfaceType);
                return TypeMapping
                    .GetOrCreateProxyTypeMapping<RoslynProxyTypeMap>()
                    .TryGetValue(
                        requestedType,
                        out implementationType);
            }
        }

        public sealed class RoslynRemoteException : Exception
        {
            public RoslynRemoteException(string message, int status)
                : base(message)
            {
                Status = status;
            }

            public int Status { get; }
        }

        public static unsafe class RoslynFacadeRuntime
        {
            private static readonly StrategyBasedComWrappers s_comWrappers = new();
            private static readonly AsyncLocal<IRoslynControlVtbl?> s_current = new();
            private static IRoslynControlVtbl? s_controlVtbl;

            public static IRoslynControlVtbl GetOrCreateControlVtbl(
                nint instance)
            {
                if (instance == 0)
                {
                    throw new ArgumentNullException(nameof(instance));
                }

                var controlVtbl = (IRoslynControlVtbl)s_comWrappers
                    .GetOrCreateObjectForComInstance(
                        instance,
                        CreateObjectFlags.None);
                int status = controlVtbl.GetManifestIdentity(
                    out long identityLow,
                    out long identityHigh);
                if (status != RoslynAbi.Success ||
                    identityLow != RoslynAbi.ManifestIdentityLow ||
                    identityHigh != RoslynAbi.ManifestIdentityHigh)
                {
                    throw new InvalidOperationException(
                        "The compiler Roslyn projection manifest is incompatible.");
                }

                s_controlVtbl ??= controlVtbl;
                if (!ReferenceEquals(s_controlVtbl, controlVtbl))
                {
                    // Handles, the proxy cache, and proxy equality are all
                    // keyed on the handle alone, which is only sound because
                    // a module talks to exactly one compiler. Nothing can
                    // produce a second control today, since RoslynInterop
                    // .Shared is a process singleton, so this is the
                    // assertion that keeps the assumption true rather than a
                    // case to handle. Failing here beats resolving one
                    // compiler's handle against another's table.
                    throw new InvalidOperationException(
                        "A second compiler Roslyn control vtbl was supplied " +
                        "to this analyzer module. Handles are only " +
                        "meaningful against the control that issued them.");
                }

                return controlVtbl;
            }

            public static IRoslynControlVtbl GetCurrentControlVtbl() =>
                s_current.Value ?? throw new InvalidOperationException(
                    "No compiler Roslyn control vtbl is active.");

            public static IDisposable Enter(IRoslynControlVtbl controlVtbl)
            {
                ArgumentNullException.ThrowIfNull(controlVtbl);
                IRoslynControlVtbl? previous = s_current.Value;
                s_current.Value = controlVtbl;
                return new Scope(previous);
            }

            public static unsafe void ThrowIfFailed(
                IRoslynControlVtbl controlVtbl,
                int status)
            {
                if (status == RoslynAbi.Success)
                {
                    return;
                }

                int queryStatus = controlVtbl.CopyLastErrorUtf16(
                    0,
                    0,
                    out int charCount,
                    out RoslynRemoteErrorKind errorKind);
                if (queryStatus != RoslynAbi.Success || charCount < 0)
                {
                    throw new RoslynRemoteException(
                        $"Remote Roslyn operation failed with 0x{status:x8}; " +
                        "its error details could not be queried.",
                        status);
                }

                string message = charCount == 0
                    ? $"Remote Roslyn operation failed with 0x{status:x8}."
                    : string.Create(
                        charCount,
                        (controlVtbl, errorKind, status),
                        static (destination, state) =>
                        {
                            fixed (char* buffer = destination)
                            {
                                int copyStatus =
                                    state.controlVtbl.CopyLastErrorUtf16(
                                        (nint)buffer,
                                        destination.Length,
                                        out int copiedCharCount,
                                        out RoslynRemoteErrorKind copiedErrorKind);
                                if (copyStatus != RoslynAbi.Success ||
                                    copiedCharCount != destination.Length ||
                                    copiedErrorKind != state.errorKind)
                                {
                                    throw new RoslynRemoteException(
                                        $"Remote Roslyn operation failed with 0x{state.status:x8}; " +
                                        "its error details changed while being copied.",
                                        state.status);
                                }
                            }
                        });
                throw errorKind switch
                {
                    RoslynRemoteErrorKind.Argument =>
                        new ArgumentException(message),
                    RoslynRemoteErrorKind.ObjectDisposed =>
                        new ObjectDisposedException("Roslyn facade", message),
                    RoslynRemoteErrorKind.Unsupported =>
                        new PlatformNotSupportedException(message),
                    RoslynRemoteErrorKind.OperationCanceled =>
                        new OperationCanceledException(message),
                    _ => new RoslynRemoteException(message, status),
                };
            }

            public static T UnsupportedStaticField<T>(string message) =>
                throw new PlatformNotSupportedException(message);

            public static unsafe string? ReadUtf16String(
                IRoslynControlVtbl controlVtbl,
                CopyUtf16String copy)
            {
                ArgumentNullException.ThrowIfNull(controlVtbl);
                ArgumentNullException.ThrowIfNull(copy);

                int status = copy(0, 0, out int charCount);
                ThrowIfFailed(controlVtbl, status);
                if (charCount == -1)
                {
                    return null;
                }

                if (charCount < -1)
                {
                    throw new InvalidOperationException(
                        "The remote UTF-16 string length is invalid.");
                }

                if (charCount == 0)
                {
                    return string.Empty;
                }

                return string.Create(
                    charCount,
                    (controlVtbl, copy),
                    static (destination, state) =>
                    {
                        fixed (char* buffer = destination)
                        {
                            int copyStatus = state.copy(
                                (nint)buffer,
                                destination.Length,
                                out int copiedCharCount);
                            ThrowIfFailed(state.controlVtbl, copyStatus);
                            if (copiedCharCount != destination.Length)
                            {
                                throw new InvalidOperationException(
                                    "The remote UTF-16 string changed while being copied.");
                            }
                        }
                    });
            }

            public static long CreateSourceTextHandle(
                IRoslynControlVtbl controlVtbl,
                string text,
                int checksumAlgorithm)
            {
                ArgumentNullException.ThrowIfNull(text);
                long result;
                fixed (char* buffer = text)
                {
                    int status = controlVtbl.CreateSourceTextUtf16(
                        (nint)buffer,
                        text.Length,
                        checksumAlgorithm,
                        out result);
                    ThrowIfFailed(controlVtbl, status);
                }

                return result;
            }

            /// <summary>
            /// Rebuilds a boxed C# constant in the analyzer's heap from its
            /// tag and payload.
            /// </summary>
            /// <remarks>
            /// The result has to be a genuine boxed primitive, not a proxy:
            /// analyzers write <c>(int)operation.ConstantValue.Value</c>,
            /// <c>value is string text</c>, and
            /// <c>value.Equals(0)</c> against it. Both modules share the
            /// framework's definition of these types, so the clone is exact
            /// and identity is not observable on a box.
            /// </remarks>
            /// <summary>
            /// Whether the compiler-side object behind a handle is of the type
            /// a vtbl id names.
            /// </summary>
            public static bool IsObjectType(
                IRoslynControlVtbl controlVtbl,
                long handle,
                long vtblIdLow,
                long vtblIdHigh)
            {
                int status = controlVtbl.IsObjectType(
                    handle,
                    vtblIdLow,
                    vtblIdHigh,
                    out int isType);
                ThrowIfFailed(controlVtbl, status);
                return isType != 0;
            }

            public static object? ReadConstant(
                IRoslynControlVtbl controlVtbl,
                RoslynConstantKind kind,
                long low,
                long high) =>
                kind switch
                {
                    RoslynConstantKind.Null => null,
                    RoslynConstantKind.Boolean => low != 0,
                    RoslynConstantKind.SByte => (sbyte)low,
                    RoslynConstantKind.Byte => (byte)low,
                    RoslynConstantKind.Int16 => (short)low,
                    RoslynConstantKind.UInt16 => (ushort)low,
                    RoslynConstantKind.Int32 => (int)low,
                    RoslynConstantKind.UInt32 => (uint)low,
                    RoslynConstantKind.Int64 => low,
                    RoslynConstantKind.UInt64 => (ulong)low,
                    RoslynConstantKind.Char => (char)low,
                    RoslynConstantKind.Single =>
                        BitConverter.Int32BitsToSingle((int)low),
                    RoslynConstantKind.Double =>
                        BitConverter.Int64BitsToDouble(low),
                    RoslynConstantKind.Decimal => new decimal(
                    [
                        (int)low,
                        (int)(low >> 32),
                        (int)high,
                        (int)(high >> 32),
                    ]),
                    RoslynConstantKind.DateTime =>
                        DateTime.FromBinary(low),
                    RoslynConstantKind.String => ReadUtf16String(
                        controlVtbl,
                        (nint buffer, int bufferLength, out int requiredLength) =>
                            controlVtbl.CopyConstantStringUtf16(
                                low,
                                buffer,
                                bufferLength,
                                out requiredLength)),
                    _ => throw new InvalidOperationException(
                        $"The remote constant kind '{kind}' is not known to " +
                        "this module."),
                };

            public static string[] ReadStringCollection(
                IRoslynControlVtbl controlVtbl,
                long handle)
            {
                int status = controlVtbl.GetCollectionCount(
                    handle,
                    out int count);
                ThrowIfFailed(controlVtbl, status);
                if (count < 0)
                {
                    throw new InvalidOperationException(
                        "The remote collection length is invalid.");
                }

                var result = new string[count];
                for (int index = 0; index < result.Length; index++)
                {
                    int itemIndex = index;
                    result[index] = ReadUtf16String(
                        controlVtbl,
                        (nint buffer, int bufferLength, out int requiredLength) =>
                            controlVtbl.CopyStringCollectionItemUtf16(
                                handle,
                                itemIndex,
                                buffer,
                                bufferLength,
                                out requiredLength)) ??
                        throw new InvalidOperationException(
                            "The remote string collection contains null.");
                }

                return result;
            }

            public static RoslynStringCollection ReadStringCollectionProxy(
                IRoslynControlVtbl controlVtbl,
                long handle) =>
                new(controlVtbl, handle);

            public static long CreateObjectCollectionHandle(
                IRoslynControlVtbl controlVtbl,
                long[] handles)
            {
                ArgumentNullException.ThrowIfNull(handles);
                long result;
                fixed (long* buffer = handles)
                {
                    int status = controlVtbl.CreateObjectCollection(
                        (nint)buffer,
                        handles.Length,
                        out result);
                    ThrowIfFailed(controlVtbl, status);
                }

                return result;
            }

            public static T[] ReadObjectCollection<T>(
                IRoslynControlVtbl controlVtbl,
                long handle,
                Func<IRoslynControlVtbl, long, T> createProxy)
            {
                ArgumentNullException.ThrowIfNull(createProxy);
                int status = controlVtbl.GetCollectionCount(
                    handle,
                    out int count);
                ThrowIfFailed(controlVtbl, status);
                if (count < 0)
                {
                    throw new InvalidOperationException(
                        "The remote collection length is invalid.");
                }

                var result = new T[count];
                for (int index = 0; index < result.Length; index++)
                {
                    status = controlVtbl.GetObjectCollectionItem(
                        handle,
                        index,
                        out long itemHandle);
                    ThrowIfFailed(controlVtbl, status);
                    result[index] = createProxy(controlVtbl, itemHandle);
                }

                return result;
            }

            public static T GetWellKnownObject<T>(
                RoslynWellKnownObject kind,
                Func<IRoslynControlVtbl, long, T> createProxy)
            {
                ArgumentNullException.ThrowIfNull(createProxy);
                IRoslynControlVtbl controlVtbl = GetCurrentControlVtbl();
                int status = controlVtbl.GetWellKnownObject(
                    kind,
                    out long handle);
                ThrowIfFailed(controlVtbl, status);
                return createProxy(controlVtbl, handle);
            }

            private sealed class Scope(IRoslynControlVtbl? previous) : IDisposable
            {
                private IRoslynControlVtbl? _previous = previous;
                private bool _disposed;

                public void Dispose()
                {
                    if (_disposed)
                    {
                        return;
                    }

                    s_current.Value = _previous;
                    _previous = null;
                    _disposed = true;
                }
            }
        }
        """;
    }

    private static string EmitVtblFactory(ProjectionModel model)
    {
        var builder = new IndentingBuilder();
        builder.AppendLine("using System.Runtime.CompilerServices;");
        builder.AppendLine("using System.Runtime.InteropServices;");
        builder.AppendLine("using System.Runtime.InteropServices.Marshalling;");
        builder.AppendLine("using RoslynAot.Abi;");
        builder.AppendLine("");
        builder.AppendLine("namespace RoslynAot.RoslynFacade;");
        builder.AppendLine("");
        builder.AppendLine("public static class RoslynVtblFactory");
        builder.AppendLine("{");
        builder.Indent();
        builder.AppendLine(
            "private static readonly StrategyBasedComWrappers s_comWrappers = new();");
        builder.AppendLine(
            "private static readonly ConditionalWeakTable<IRoslynControlVtbl, VtblCache> s_caches = new();");
        builder.AppendLine("");
        foreach (VtblProjection vtbl in model.Vtbls)
        {
            (long low, long high) = GetVtblIdParts(vtbl);
            builder.AppendLine(
                $"public static {vtbl.Name} " +
                $"{vtbl.FactoryMethodName}(");
            builder.Indent();
            builder.AppendLine("IRoslynControlVtbl controlVtbl)");
            builder.Dedent();
            builder.AppendLine("{");
            builder.Indent();
            builder.AppendLine(
                "ArgumentNullException.ThrowIfNull(controlVtbl);");
            builder.AppendLine($"return GetVtbl<{vtbl.Name}>(");
            builder.Indent();
            builder.AppendLine("controlVtbl,");
            builder.AppendLine($"{low}L,");
            builder.AppendLine($"{high}L);");
            builder.Dedent();
            builder.Dedent();
            builder.AppendLine("}");
            builder.AppendLine("");
        }

        builder.AppendLine(
            """
            private static T GetVtbl<T>(
                IRoslynControlVtbl controlVtbl,
                long vtblIdLow,
                long vtblIdHigh)
                where T : class
            {
                object vtbl = s_caches.GetValue(controlVtbl, static _ => new VtblCache())
                    .GetOrCreate(controlVtbl, vtblIdLow, vtblIdHigh);
                return vtbl as T ?? throw new InvalidOperationException(
                    $"The resolved Roslyn vtable does not implement '{typeof(T).FullName}'.");
            }

            private sealed class VtblCache
            {
                private readonly Dictionary<(long Low, long High), object> _vtbls = [];

                public object GetOrCreate(
                    IRoslynControlVtbl controlVtbl,
                    long vtblIdLow,
                    long vtblIdHigh)
                {
                    lock (_vtbls)
                    {
                        if (_vtbls.TryGetValue((vtblIdLow, vtblIdHigh), out object? existing))
                        {
                            return existing;
                        }

                        int status = controlVtbl.GetVtbl(vtblIdLow, vtblIdHigh, out nint pointer);
                        RoslynFacadeRuntime.ThrowIfFailed(controlVtbl, status);
                        if (pointer == 0)
                        {
                            throw new InvalidOperationException("The compiler returned no Roslyn vtable pointer.");
                        }

                        try
                        {
                            object created = s_comWrappers.GetOrCreateObjectForComInstance(
                                pointer,
                                CreateObjectFlags.None);
                            _vtbls.Add((vtblIdLow, vtblIdHigh), created);
                            return created;
                        }
                        finally
                        {
                            RoslynAbi.Release(pointer);
                        }
                    }
                }
            }
            """);
        builder.Dedent();
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string EmitInventory(ProjectionModel model)
    {
        var builder = new StringBuilder();
        int supportedCount =
            model.Calls.Count(call => call.IsSupported);
        builder.AppendLine($"identity={model.Identity}");
        builder.AppendLine($"supported={supportedCount}");
        builder.AppendLine(
            $"unsupported={model.Calls.Count - supportedCount}");
        builder.AppendLine(
            $"overrides={model.Calls.Count(call => call.OverrideReason is not null)}");
        builder.AppendLine($"vtbls={model.Vtbls.Count}");
        builder.AppendLine($"types={model.Types.Count}");
        builder.AppendLine(
            "declaredOwnership=" +
            model.Types.Count(type => type.OwnershipDeclared));
        builder.AppendLine(
            $"reachableTypes={model.Types.Count(type => type.IsReachable)}");
        builder.AppendLine(
            "unreachableCalls=" +
            model.Calls.Count(call =>
                call.IsSupported && !model.IsReachable(call)));

        IReadOnlyList<ForeignTypeUse> foreignTypes =
            ProjectionForeignTypes.Collect(model);
        builder.AppendLine($"foreignTypes={foreignTypes.Count}");
        builder.AppendLine(
            "declaredForeignTypes=" +
            foreignTypes.Count(use => use.Declared));
        foreach (ForeignTransport transport in Enum.GetValues<ForeignTransport>())
        {
            builder.AppendLine(
                $"foreign{transport}=" +
                foreignTypes.Count(use => use.Entry.Transport == transport) +
                "/" +
                foreignTypes.Count(use =>
                    use.Entry.Transport == transport && use.SupportedUses > 0));
        }

        builder.AppendLine();

        // The types the boundary cannot substitute itself for. Reported with
        // total and supported use counts because the difference is the point:
        // a class with uses but no supported uses is work not yet started, and
        // that is where the expensive surprises are.
        foreach (ForeignTypeUse use in foreignTypes)
        {
            builder.Append("FOREIGN transport=");
            builder.Append(use.Entry.Transport);
            builder.Append(use.Declared ? " (declared)" : " (derived)");
            builder.Append(" uses=");
            builder.Append(use.Uses);
            builder.Append(" supportedUses=");
            builder.Append(use.SupportedUses);
            builder.Append(" id=");
            builder.AppendLine(use.CanonicalId);
        }

        builder.AppendLine();

        foreach (TypeProjection type in model.Types)
        {
            builder.Append("TYPE ownership=");
            builder.Append(type.Ownership);
            builder.Append(type.OwnershipDeclared
                ? " (declared)"
                : " (derived)");
            builder.Append(" shape=");
            builder.Append(type.Shape);
            builder.Append(" proxy=");
            builder.Append(type.UsesDynamicInterfaceProxy
                ? "dynamic"
                : type.RequiresProxy
                    ? "yes"
                    : "no");
            builder.Append(" instanceVtbl=");
            builder.Append(type.InstanceVtbl?.Name ?? "none");
            builder.Append(" typeVtbl=");
            builder.Append(type.TypeVtbl?.Name ?? "none");
            builder.Append(" reachedBy=");
            builder.Append(type.ReachedBy ?? "unreachable");
            // The ownership reason is last because it is the one free-text
            // field on the line: every entry needs one, and until the JSON
            // manifest was removed this was the only place it was recorded.
            builder.Append(" ownershipReason=");
            builder.Append(type.OwnershipReason);
            builder.Append(" id=");
            builder.AppendLine(type.CanonicalId);
        }

        builder.AppendLine();

        foreach (ProjectedCall operation in model.Calls)
        {
            builder.Append(operation.IsSupported
                ? "SUPPORTED"
                : "UNSUPPORTED");
            builder.Append(" name=");
            builder.Append(operation.GeneratedName);
            builder.Append(" strategy=");
            builder.Append(operation.Strategy);
            builder.Append(" vtbl=");
            builder.Append(operation.Vtbl?.Name ?? "none");
            builder.Append(" receiver=");
            builder.Append(
                operation.Receiver?.InventoryName ?? "none");
            builder.Append(" parameters=[");
            builder.Append(
                string.Join(
                    ",",
                    operation.Parameters.Select(parameter =>
                        $"{parameter.Symbol.Name}:{parameter.AbiType.InventoryName}")));
            builder.Append("] return=");
            builder.Append(operation.ReturnValue.InventoryName);
            if (operation.UnsupportedReason is not null)
            {
                builder.Append(" reason=");
                builder.Append(operation.UnsupportedReason);
            }

            if (operation.OverrideReason is not null)
            {
                builder.Append(" override=");
                builder.Append(operation.OverrideReason);
            }

            builder.Append(" wire=");
            builder.Append(operation.WireSignature);
            builder.Append(" id=");
            builder.AppendLine(operation.CanonicalId);
        }

        return builder.ToString();
    }

    private static string GetSourceType(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private static string RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
        return path;
    }

    private static void WriteGeneratedFile(string path, string content)
    {
        string? directory = Path.GetDirectoryName(path);
        if (directory is not null)
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            path,
            CSharpFileBuilder.DefaultFileHeader +
            content.TrimEnd().ReplaceLineEndings("\n") +
            "\n");
    }
}
